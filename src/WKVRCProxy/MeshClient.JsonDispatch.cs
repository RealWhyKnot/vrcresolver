using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal sealed partial class MeshClient
{
    private async Task DispatchFrameAsync(byte[] payload, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (Exception ex)
        {
            // Server protocol regression / framing bug. Without this log a
            // malformed frame would drop pending TCS to time out 10s later as
            // server_unreachable with no breadcrumb. Dedupe by exception type
            // so a flapping server can't fill the scrollback.
            LogParseFailure(ex, payload);
            return;
        }

        string action = "";
        if (doc.RootElement.TryGetProperty("action", out var actionEl) && actionEl.ValueKind == JsonValueKind.String)
            action = actionEl.GetString() ?? "";

        switch (action)
        {
            case WireConstants.ActionResolved:
            case WireConstants.ActionFallbackNative:
            {
                // Extract id, reason, and (on `resolved`) the resolved URL
                // from the parsed doc; then dispose the doc and hand the
                // verified raw frame bytes to the pending TCS. Caller writes
                // bytes through to the pipe — no JsonDocument re-encode on
                // the hot path.
                string id = "";
                if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    id = idEl.GetString() ?? "";

                string? reason = null;
                if (doc.RootElement.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
                    reason = reasonEl.GetString();

                string? resolvedUrl = null;
                if (action == WireConstants.ActionResolved
                    && doc.RootElement.TryGetProperty("url", out var urlEl)
                    && urlEl.ValueKind == JsonValueKind.String)
                {
                    resolvedUrl = urlEl.GetString();
                }

                // bytes_estimate is a v2 response field (server's stream-size
                // estimate). Sum across resolves for the heartbeat line so
                // the operator can see aggregate "stream-bytes" served. Only
                // counted on `resolved` (fallback_native means og takes over
                // and the bytes don't go through us).
                if (action == WireConstants.ActionResolved
                    && doc.RootElement.TryGetProperty("bytes_estimate", out var beEl)
                    && beEl.ValueKind == JsonValueKind.Number
                    && beEl.TryGetInt64(out long bytesEstimate))
                {
                    WatchdogStats.RecordBytesEstimate(bytesEstimate);
                }

                if (action == WireConstants.ActionFallbackNative)
                    LogFallbackNative(id, reason);

                // doc no longer needed past this point — payload bytes carry
                // everything LocalIpcServer needs to forward to the wrapper.
                doc.Dispose();

                if (string.IsNullOrEmpty(id)) return;

                // Pop the inflight cid so VrcLogMonitor can later look it up by
                // the resolved URL. Only on `resolved` — fallback_native means
                // the patched yt-dlp re-runs vanilla, so the URL AVPro
                // ultimately opens isn't ours to attribute.
                _inflightCids.TryRemove(id, out var cid);
                if (action == WireConstants.ActionResolved
                    && !string.IsNullOrEmpty(cid)
                    && !string.IsNullOrEmpty(resolvedUrl))
                {
                    RememberResolvedUrlCid(resolvedUrl!, cid);
                }

                if (_pending.TryRemove(id, out var tcs))
                {
                    if (action == WireConstants.ActionResolved) _reconnectAttempt = 0;
                    tcs.TrySetResult(new MeshResolveResult(payload, action, reason));
                }
                return;
            }
            case WireConstants.ActionWelcome:
            {
                WelcomeFrame? welcome = null;
                try { welcome = JsonSerializer.Deserialize(payload, MeshJsonContext.Default.WelcomeFrame); }
                catch (Exception ex)
                {
                    ConsoleUx.Warn(LogComponent.Mesh, "welcome parse failed -- assuming v1 server: "
                        + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
                    // Pin protocol version to v1 so subsequent ResolveAsync calls
                    // don't get stuck waiting for a welcome that never arrives in
                    // a parseable form.
                    Interlocked.CompareExchange(ref _serverProtocolVersion, 1, 0);
                }

                if (welcome != null)
                {
                    // Clamp to [1, ClientProtocolVersion]:
                    //   - 0 / missing field demoting us back to "pre-welcome"
                    //     would re-arm the 1s-timer's CompareExchange branch
                    //     and confuse routing decisions. Force at least 1.
                    //   - We can't speak anything newer than ClientProtocolVersion;
                    //     advertising support for v999 would be a lie.
                    int negotiated = Math.Clamp(
                        welcome.ProtocolVersion,
                        1,
                        WireConstants.ClientProtocolVersion);
                    Interlocked.Exchange(ref _serverProtocolVersion, negotiated);
                    _serverNode = welcome.Node;
                    _serverFeatures = welcome.Features;
                    _warpActive = welcome.WarpActive;
                    _serverVersion = welcome.ServerVersion;
                    _ytDlpVersion = welcome.YtDlpVersion;

                    // v3.1: capture the post-welcome wire format the
                    // server picked from our accept_formats list. Null
                    // / missing field = "json" (v3.0 server, or v3.1
                    // server we sent json-only opt-out to).
                    _negotiatedFormat = welcome.NegotiatedFormat ?? WireConstants.FormatJson;
                    _isMsgpackFormat = string.Equals(_negotiatedFormat, WireConstants.FormatMsgpack, StringComparison.Ordinal);
                    Logger.WriteFileOnly("[mesh][v3.1] negotiated_format=" + _negotiatedFormat
                        + " isMsgpack=" + _isMsgpackFormat);

                    // Spec marks engines + features required on welcome.
                    // Surface a warning if either is null so a server-side
                    // regression that drops the field is diagnosable.
                    if (welcome.Engines == null)
                        ConsoleUx.Warn(LogComponent.Mesh, "welcome missing required field: engines");
                    if (welcome.Features == null)
                        ConsoleUx.Warn(LogComponent.Mesh, "welcome missing required field: features");

                    string features = welcome.Features != null && welcome.Features.Length > 0
                        ? "[" + string.Join(",", welcome.Features) + "]"
                        : "[]";
                    ConsoleUx.Write(
                        LogComponent.Mesh,
                        "welcome node=" + (welcome.Node ?? "?") +
                        " v=" + welcome.ProtocolVersion + " (negotiated=" + negotiated + ")" +
                        " server=" + (welcome.ServerVersion ?? "?") +
                        " yt-dlp=" + (welcome.YtDlpVersion ?? "?") +
                        " warp_active=" + (welcome.WarpActive?.ToString() ?? "?") +
                        " features=" + LogUtil.SanitizeForConsole(features, 240));

                    // v3: persist the welcome contents keyed by hash so
                    // the next reconnect can offer it back in client_hello
                    // and let the server reply with the smaller
                    // welcome_cached frame. Only on v3 connections AND
                    // when the server actually sent a hash — v2 servers
                    // don't and shouldn't cache.
                    if (_isV3Connection && !string.IsNullOrEmpty(welcome.WelcomeHash)
                        && !string.IsNullOrEmpty(_currentNodeHost))
                    {
                        try { _welcomeCache.Store(_currentNodeHost, welcome, welcome.WelcomeHash!); }
                        catch (Exception storeEx)
                        {
                            Logger.WriteFileOnly("[mesh][v3] cache store failed: "
                                + storeEx.GetType().Name + ": "
                                + LogUtil.SanitizeForConsole(storeEx.Message, 160));
                        }
                    }
                }
                _welcomeTcs?.TrySetResult(welcome);
                QueueHelperStatusRefresh(force: true);
                doc.Dispose();
                return;
            }
            case WireConstants.ActionWelcomeCached:
            {
                // v3: server confirmed our cached welcome_hash matched.
                // Hydrate per-connection state from the local cache;
                // server only sent the dynamic fields (warp_active +
                // node label) in this small frame. Engines / features /
                // version strings come from the cache entry.
                if (!_isV3Connection)
                {
                    ConsoleUx.Warn(LogComponent.Mesh, "welcome_cached received on non-v3 connection -- protocol error, reconnecting");
                    try { _ws?.Abort(); } catch { /* ignore */ }
                    doc.Dispose();
                    return;
                }
                WelcomeCachedFrame? cached = null;
                try { cached = JsonSerializer.Deserialize(payload, MeshJsonContext.Default.WelcomeCachedFrame); }
                catch (Exception ex)
                {
                    ConsoleUx.Warn(LogComponent.Mesh, "welcome_cached parse failed: "
                        + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
                    Interlocked.CompareExchange(ref _serverProtocolVersion, 1, 0);
                    _welcomeTcs?.TrySetResult(null);
                    doc.Dispose();
                    return;
                }

                var entry = !string.IsNullOrEmpty(_currentNodeHost)
                    ? _welcomeCache.Get(_currentNodeHost)
                    : null;
                if (entry == null)
                {
                    // Server claimed cache hit but we have nothing to
                    // hydrate from — sync drift (file deleted between
                    // client_hello send and this dispatch, or another
                    // process clobbered the cache). Drop any stale
                    // slot and force a clean reconnect with null hash;
                    // the server will resend the full welcome.
                    if (!string.IsNullOrEmpty(_currentNodeHost))
                        _welcomeCache.Invalidate(_currentNodeHost);
                    ConsoleUx.Warn(LogComponent.Mesh, "welcome_cached but local entry missing -- invalidating + reconnecting");
                    try { _ws?.Abort(); } catch { /* ignore */ }
                    doc.Dispose();
                    return;
                }

                int negotiated = Math.Clamp(
                    cached?.ProtocolVersion ?? entry.ProtocolVersion,
                    1,
                    WireConstants.ClientProtocolVersion);
                Interlocked.Exchange(ref _serverProtocolVersion, negotiated);
                _serverNode = cached?.Node ?? entry.Node;
                _serverFeatures = entry.Features;
                _warpActive = cached?.WarpActive ?? entry.WarpActive;
                _serverVersion = entry.ServerVersion;
                _ytDlpVersion = entry.YtDlpVersion;

                // v3.1: server's negotiated format is per-connection,
                // never cached — read from the dynamic fields the
                // welcome_cached frame carries. Null / missing field
                // means json (v3.0 server, or v3.1 server we sent
                // json-only opt-out to).
                _negotiatedFormat = cached?.NegotiatedFormat ?? WireConstants.FormatJson;
                _isMsgpackFormat = string.Equals(_negotiatedFormat, WireConstants.FormatMsgpack, StringComparison.Ordinal);

                Logger.WriteFileOnly("[mesh][v3] welcome_cached hit node="
                    + (_serverNode ?? "?") + " v=" + negotiated
                    + " negotiated_format=" + _negotiatedFormat
                    + " features=" + (entry.Features != null
                        ? string.Join(",", entry.Features) : "<none>"));
                // No INFO console line — equivalent state was already
                // cached; the user doesn't need a "still v3, still
                // connected" reminder. The connect+welcome banner
                // already fired the first time.

                // _welcomeTcs awaits a WelcomeFrame? — null is fine
                // here. ResolveAsync waiters key off _serverProtocolVersion
                // and _serverFeatures, both of which are now set.
                _welcomeTcs?.TrySetResult(null);
                QueueHelperStatusRefresh(force: true);
                doc.Dispose();
                return;
            }
            case WireConstants.ActionHelperTranscodeLease:
            {
                HelperTranscodeLeaseFrame? lease = null;
                try { lease = JsonSerializer.Deserialize(payload, MeshJsonContext.Default.HelperTranscodeLeaseFrame); }
                catch (Exception ex)
                {
                    ConsoleUx.Warn(LogComponent.Mesh, "helper lease parse failed: "
                        + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
                }
                if (lease != null)
                    QueueHelperLease(lease);
                doc.Dispose();
                return;
            }
            case WireConstants.ActionResolveLog:
                LogResolveLogFrame(doc.RootElement);
                doc.Dispose();
                return;
            case WireConstants.ActionPong:
                _lastPongUtc = DateTime.UtcNow;
                doc.Dispose();
                return;
            case WireConstants.ActionPing:
                doc.Dispose();
                try
                {
                    // Snapshot _ws before send — same TOCTOU concern as the
                    // heartbeat loop's snapshot.
                    var pongWs = _ws;
                    if (pongWs is { State: WebSocketState.Open })
                    {
                        await SendTextFrameAsync(PongFrame, ct).ConfigureAwait(false);
                    }
                }
                catch { /* heartbeat will catch dead socket */ }
                return;
            case WireConstants.ActionHelperChallenge:
            {
                HelperChallengeFrame? challenge = null;
                try { challenge = JsonSerializer.Deserialize(payload, MeshJsonContext.Default.HelperChallengeFrame); }
                catch (Exception ex)
                {
                    ConsoleUx.Warn(LogComponent.Mesh, "helper_challenge parse failed: "
                        + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
                    doc.Dispose();
                    return;
                }
                doc.Dispose();
                if (challenge == null) return;

                AppSettings settings = AppSettingsStore.Shared.Snapshot();
                string? trustKey = settings.Helper.TrustKey;
                if (string.IsNullOrWhiteSpace(trustKey))
                {
                    Logger.WriteDiagnostic(
                        LogComponent.Helper,
                        "[mesh][helper] helper_challenge_received but no trust key configured",
                        "helper_challenge_received but no trust key configured");
                    return;
                }

                string signature = ComputeChallengeSignature(challenge.Nonce, _clientId, trustKey);
                var response = new HelperChallengeResponseFrame { Signature = signature };
                try
                {
                    byte[] respBytes = JsonSerializer.SerializeToUtf8Bytes(
                        response, MeshJsonContext.Default.HelperChallengeResponseFrame);
                    await SendTextFrameAsync(respBytes, ct).ConfigureAwait(false);
                    Logger.WriteDiagnostic(
                        LogComponent.Helper,
                        "[mesh][helper] helper_challenge_responded",
                        "helper_challenge_responded");
                }
                catch (Exception ex)
                {
                    Logger.WriteFileOnly("[mesh][helper] helper_challenge_response send failed: "
                        + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
                }
                return;
            }
            case WireConstants.ActionHelperTrustGranted:
            {
                _isTrusted = true;
                ConsoleUx.Write(LogComponent.Helper, "helper_trust_granted received from server");
                doc.Dispose();
                return;
            }
            default:
                // Server-supplied string — strip control chars + truncate so a
                // hostile or buggy server can't inject ANSI escapes into the
                // user's console window.
                ConsoleUx.Warn(LogComponent.Mesh, "unknown action -- discarding: "
                    + LogUtil.SanitizeForConsole(action, 64));
                doc.Dispose();
                return;
        }
    }


    private static void LogResolveLogFrame(JsonElement root)
    {
        string id = "";
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            id = idEl.GetString() ?? "";

        string level = "info";
        if (root.TryGetProperty("level", out var levelEl) && levelEl.ValueKind == JsonValueKind.String)
            level = levelEl.GetString() ?? "info";

        string message = "";
        if (root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            message = msgEl.GetString() ?? "";

        Logger.WriteFileOnly(
            "[mesh][resolve_log] id=" + LogUtil.SanitizeForConsole(id, 32) +
            " level=" + LogUtil.SanitizeForConsole(level, 16) +
            " " + LogUtil.SanitizeForConsole(message, 240));
    }



    // Dedupe parse-failure logs by exception type so a flapping server can't
    // fill scrollback. One log per (type) per minute, plus a counter.
    private readonly Dictionary<string, (DateTime LastEmit, int Count)> _parseFailDedupe = new();
    private void LogParseFailure(Exception ex, byte[] payload)
    {
        string key = ex.GetType().Name;
        var now = DateTime.UtcNow;
        bool emit;
        int count;
        lock (_parseFailDedupe)
        {
            if (!_parseFailDedupe.TryGetValue(key, out var entry)
                || (now - entry.LastEmit).TotalMinutes >= 1)
            {
                count = entry.Count + 1;
                _parseFailDedupe[key] = (now, count);
                emit = true;
            }
            else
            {
                count = entry.Count + 1;
                _parseFailDedupe[key] = (entry.LastEmit, count);
                emit = false;
            }
        }
        if (emit)
        {
            ConsoleUx.Warn(
                LogComponent.Mesh,
                "frame parse failed (" + key + " x" + count + " in last min): " +
                LogUtil.SanitizeForConsole(ex.Message, 80) +
                " — preview=" + LogUtil.PayloadPreview(payload, 120));
        }
    }

    // HMAC-SHA256(key=trustKeyBytes, data=nonce+"\n"+clientId). Returns lowercase hex.
    // Wire contract: must match server-side verification exactly.
    internal static string ComputeChallengeSignature(string nonce, string clientId, string trustKey)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(trustKey);
        byte[] data = Encoding.UTF8.GetBytes(nonce + "\n" + clientId);
        byte[] hash = HMACSHA256.HashData(keyBytes, data);
        return Convert.ToHexStringLower(hash);
    }

}
