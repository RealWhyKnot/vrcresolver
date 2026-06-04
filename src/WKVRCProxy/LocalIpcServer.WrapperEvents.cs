using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal sealed partial class LocalIpcServer : IDisposable
{
    private static bool IsRetryableFallback(string? reason) =>
            reason == WireConstants.FallbackDiscoveryInProgress
            || reason == WireConstants.FallbackServerUnreachable;

    private void HandleWrapperEvent(WrapperEventNotify notify)
    {
        if (string.Equals(notify.Action, WireConstants.ActionWrapperOgFailedNotify, StringComparison.Ordinal))
        {
            HandleOgFailedNotify(notify);
            return;
        }
        HandleOgFallbackNotify(notify);
    }
    // Cheap pre-deserialize peek: is this one of the wrapper's notify
    // frames (og_fallback_notify or wrapper_og_failed)? Avoids parsing
    // as ResolveRequest first (which would drop the unrecognized fields
    // into [JsonExtensionData] instead of routing to the dispatch).
    private static bool LooksLikeWrapperEventNotify(string line)
    {
        int probeLen = Math.Min(line.Length, 256);
        var head = line.AsSpan(0, probeLen);
        return head.IndexOf("og_fallback_notify".AsSpan(), StringComparison.Ordinal) >= 0
            || head.IndexOf("wrapper_og_failed".AsSpan(), StringComparison.Ordinal) >= 0;
    }

    private static void HandleOgFallbackNotify(WrapperEventNotify notify)
    {
        string host = string.IsNullOrEmpty(notify.Url) ? "<no-url>" : ExtractHost(notify.Url);
        string reason = LogUtil.SanitizeForConsole(notify.Reason ?? "?", 32);
        // Pairs visually with the !! fallback colour on the resolve summary
        // line -- the wrapper's og fallback path is the same outcome category.
        ConsoleUx.WrapperFallback(host: host, reason: reason, elapsedMs: notify.ElapsedMs);
        Logger.WriteFileOnly(
            "[wrapper] og_fallback_notify rid=" + LogUtil.SanitizeForConsole(notify.Rid ?? "?", 16) +
            " host=" + host +
            " reason=" + reason +
            " elapsed_ms=" + notify.ElapsedMs);
    }

    private void HandleOgFailedNotify(WrapperEventNotify notify)
    {
        string host = string.IsNullOrEmpty(notify.Url) ? "<no-url>" : ExtractHost(notify.Url);
        string reason = LogUtil.SanitizeForConsole(notify.Reason ?? "?", 32);
        string preview = LogUtil.SanitizeForConsole(notify.ErrorPreview ?? "", 80);

        // Evict any cached resolve for this URL -- the cache may have held
        // an entry from before the upstream blocker (CF challenge, sign-in
        // gate) appeared. Next VRChat retry for the same URL will skip the
        // cache and re-hit the mesh, which by then may have completed
        // discovery_in_progress or chosen a different strategy.
        int evicted = 0;
        if (!string.IsNullOrEmpty(notify.Url))
        {
            try { evicted = _cache?.EvictByUrl(notify.Url) ?? 0; }
            catch { /* best-effort */ }
        }

        // Short human hint after the machine-readable token. Keeps the token in
        // the line for grep/log triage while making the cause obvious to a user
        // glancing at the console. Unknown stays bare so the line doesn't lie
        // about what we know.
        string hint = reason switch
        {
            "content_not_found" => " (video unavailable upstream)",
            "cf_403" => " (403 blocked)",
            "rate_limited" => " (rate limited)",
            "sign_in_required" => " (auth gate)",
            _ => "",
        };
        ConsoleUx.Warn(
            LogComponent.Wrapper,
            "!! og also failed " + host + " reason=" + reason + " exit=" + notify.ExitCode + hint);
        Logger.WriteFileOnly(
            "[wrapper] wrapper_og_failed rid=" + LogUtil.SanitizeForConsole(notify.Rid ?? "?", 16) +
            " host=" + host +
            " reason=" + reason +
            " exit=" + notify.ExitCode +
            " elapsed_ms=" + notify.ElapsedMs +
            " evicted=" + evicted +
            " preview=" + preview);
    }

}
