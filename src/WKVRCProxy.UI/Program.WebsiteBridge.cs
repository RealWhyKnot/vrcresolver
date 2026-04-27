using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.WindowsAPICodePack.Dialogs;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.UI;

// Partial — host-side handlers for the website-embed bridge.
//
// The flow:
//   1. The iframe at https://whyknot.dev posts { type: 'wkBridge', method, requestId, args }
//      to its parent window.
//   2. WebsiteView.vue validates event.origin against the allowed embed origin and forwards
//      the message to the host as a `WEBSITE_BRIDGE` IPC command.
//   3. This file dispatches on `method` against a strict allowlist and sends back a
//      `WEBSITE_BRIDGE_RESPONSE` with `{ requestId, ok, data?, error? }`.
//   4. WebsiteView.vue routes the response back to the iframe via postMessage with the
//      embed origin as the explicit targetOrigin.
//
// Allowed methods: PICK_FILE, COPY_TO_CLIPBOARD, OPEN_IN_BROWSER, GET_HISTORY, GET_VERSION,
// GET_LAST_LINK. Anything else is rejected with `method not allowed`.
//
// Security boundary: origin validation is done in the renderer (WebsiteView.vue) before the
// message ever reaches this dispatcher. This is the second line — even if a non-allowlisted
// method gets through, it's dropped here too. Bridge methods deliberately do NOT touch the
// resolver, updater, hosts manager, relay, or any settings-mutation surface.
[SupportedOSPlatform("windows")]
partial class Program
{
    // Tracks (method) tuples we've already warned about this session, so a misbehaving
    // page can't flood the log with denied-method noise.
    private static readonly HashSet<string> _bridgeWarnedMethods = new(StringComparer.OrdinalIgnoreCase);

    private static void HandleWebsiteBridge(JsonElement root)
    {
        string requestId = "";
        string method = "";
        try
        {
            if (!root.TryGetProperty("data", out var data))
            {
                _logger?.Warning("[WebsiteBridge] message missing data envelope.");
                return;
            }
            requestId = data.TryGetProperty("requestId", out var ridEl) ? (ridEl.GetString() ?? "") : "";
            method = data.TryGetProperty("method", out var mEl) ? (mEl.GetString() ?? "") : "";
            JsonElement args = data.TryGetProperty("args", out var aEl) ? aEl : default;

            switch (method)
            {
                case "PICK_FILE":
                    BridgePickFile(requestId, args);
                    break;
                case "COPY_TO_CLIPBOARD":
                    BridgeCopyToClipboard(requestId, args);
                    break;
                case "OPEN_IN_BROWSER":
                    BridgeOpenInBrowser(requestId, args);
                    break;
                case "GET_HISTORY":
                    BridgeGetHistory(requestId);
                    break;
                case "GET_VERSION":
                    BridgeGetVersion(requestId);
                    break;
                case "GET_LAST_LINK":
                    BridgeGetLastLink(requestId);
                    break;
                default:
                    if (_bridgeWarnedMethods.Add(method ?? "<null>"))
                    {
                        _logger?.Warning("[WebsiteBridge] rejected method '" + method + "' (not in allowlist).");
                    }
                    SendBridgeResponse(requestId, ok: false, error: "method not allowed");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning("[WebsiteBridge] dispatch failed for method '" + method + "': " + ex.Message);
            SendBridgeResponse(requestId, ok: false, error: ex.Message);
        }
    }

    private static void SendBridgeResponse(string requestId, bool ok, object? data = null, string? error = null)
    {
        SendToUi("WEBSITE_BRIDGE_RESPONSE", new
        {
            requestId,
            ok,
            data,
            error
        });
    }

    private static void BridgePickFile(string requestId, JsonElement args)
    {
        string[]? accept = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("accept", out var acceptEl)
            && acceptEl.ValueKind == JsonValueKind.Array)
        {
            accept = acceptEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
        }

        // CommonOpenFileDialog requires the STA UI thread; route through the window invoke.
        _window?.Invoke(() =>
        {
            try
            {
                using var dialog = new CommonOpenFileDialog
                {
                    IsFolderPicker = false,
                    Multiselect = false,
                    EnsureFileExists = true,
                    EnsurePathExists = true,
                };
                if (accept != null && accept.Length > 0)
                {
                    // Accept entries can be ".mp4", "*.mp4", or "video/mp4". Normalise to the
                    // ".ext" form CommonFileDialogFilter expects; mime types are dropped (the
                    // common case from the website is extension-based anyway).
                    var exts = new List<string>();
                    foreach (var a in accept)
                    {
                        if (a.Contains('/')) continue; // mime type — skip
                        var ext = a.TrimStart('*', '.');
                        if (!string.IsNullOrEmpty(ext)) exts.Add(ext);
                    }
                    if (exts.Count > 0)
                    {
                        dialog.Filters.Add(new CommonFileDialogFilter("Allowed", string.Join(";", exts)));
                    }
                }

                if (dialog.ShowDialog() == CommonFileDialogResult.Ok && !string.IsNullOrEmpty(dialog.FileName))
                {
                    var fi = new FileInfo(dialog.FileName);
                    SendBridgeResponse(requestId, ok: true, data: new
                    {
                        path = fi.FullName,
                        name = fi.Name,
                        sizeBytes = fi.Exists ? fi.Length : 0L,
                    });
                }
                else
                {
                    SendBridgeResponse(requestId, ok: false, error: "cancelled");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning("[WebsiteBridge] PICK_FILE failed: " + ex.Message);
                SendBridgeResponse(requestId, ok: false, error: ex.Message);
            }
        });
    }

    private static void BridgeCopyToClipboard(string requestId, JsonElement args)
    {
        string text = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("text", out var tEl)
            ? (tEl.GetString() ?? "") : "";
        if (string.IsNullOrEmpty(text))
        {
            SendBridgeResponse(requestId, ok: false, error: "text required");
            return;
        }
        // System.Windows.Forms.Clipboard requires STA; the Photino UI thread is STA.
        _window?.Invoke(() =>
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(text);
                SendBridgeResponse(requestId, ok: true);
            }
            catch (Exception ex)
            {
                _logger?.Warning("[WebsiteBridge] COPY_TO_CLIPBOARD failed: " + ex.Message);
                SendBridgeResponse(requestId, ok: false, error: ex.Message);
            }
        });
    }

    private static void BridgeOpenInBrowser(string requestId, JsonElement args)
    {
        string url = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("url", out var uEl)
            ? (uEl.GetString() ?? "") : "";
        if (string.IsNullOrEmpty(url))
        {
            SendBridgeResponse(requestId, ok: false, error: "url required");
            return;
        }
        // Scheme guard — only http/https. The existing OPEN_BROWSER IPC opens whatever it's
        // handed, but the bridge surface is callable from a remote origin so we tighten here.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            _logger?.Warning("[WebsiteBridge] OPEN_IN_BROWSER rejected non-http(s) url: " + url);
            SendBridgeResponse(requestId, ok: false, error: "scheme not allowed");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = parsed.ToString(), UseShellExecute = true });
            SendBridgeResponse(requestId, ok: true);
        }
        catch (Exception ex)
        {
            _logger?.Warning("[WebsiteBridge] OPEN_IN_BROWSER failed: " + ex.Message);
            SendBridgeResponse(requestId, ok: false, error: ex.Message);
        }
    }

    private static void BridgeGetHistory(string requestId)
    {
        // Mapping the C# HistoryEntry shape onto the website's expected
        // { history: [{ link, resolvedAt, type }] } envelope. wkBridge.ts filters out anything
        // missing a string `link`, so entries with no original URL are dropped here too rather
        // than letting the page do the work. `type` doubles as the cascade tier so the page
        // can label entries; `title` isn't tracked locally so it's omitted.
        var entries = (_settings?.Config.History ?? new List<HistoryEntry>())
            .Where(h => !string.IsNullOrEmpty(h.OriginalUrl))
            .Select(h => new
            {
                link = h.OriginalUrl,
                resolvedAt = h.Timestamp.ToString("O"),
                type = string.IsNullOrEmpty(h.Tier) ? null : h.Tier,
            })
            .ToList();
        SendBridgeResponse(requestId, ok: true, data: new { history = entries });
    }

    private static void BridgeGetVersion(string requestId)
    {
        string version = "unknown";
        try
        {
            string vpath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
            if (File.Exists(vpath)) version = File.ReadAllText(vpath).Trim();
        }
        catch { /* best-effort; "unknown" is a fine fallback */ }
        SendBridgeResponse(requestId, ok: true, data: new { version });
    }

    private static void BridgeGetLastLink(string requestId)
    {
        // Last successful resolution from the persisted history. Matches what the retired
        // ShareView.vue used (history.find(h => h.Success && h.OriginalUrl)) — same surface,
        // different consumer. Avoids reaching into ResolutionEngine's recent-resolutions ring,
        // which is short-TTL state for playback feedback, not a "last link" feed.
        //
        // Shape: { link, resolvedAt? } — matches the website's LastLink type. `link` is the
        // original (user-shared) URL, not the resolved manifest, since that's what the page
        // wants to surface for "share again" / re-paste flows. wkBridge.ts treats both null
        // payloads and `link === ''` as "no last link", so a missing entry returns null.
        var entry = _settings?.Config.History.FirstOrDefault(
            h => h.Success && !string.IsNullOrEmpty(h.OriginalUrl));
        if (entry == null)
        {
            SendBridgeResponse(requestId, ok: true, data: (object?)null);
            return;
        }
        SendBridgeResponse(requestId, ok: true, data: new
        {
            link = entry.OriginalUrl,
            resolvedAt = entry.Timestamp.ToString("O"),
        });
    }
}
