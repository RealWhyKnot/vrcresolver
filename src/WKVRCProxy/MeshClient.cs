using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Persistent reconnecting WebSocket client to whyknot.dev's mesh endpoint.
//
// Apex-302 discovery: GET https://whyknot.dev/ with auto-redirect off, parse
// Location for the assigned nodeN.whyknot.dev hostname. Cached in memory only;
// re-resolved if reconnect attempts keep failing for more than 5 min straight.
//
// v2 protocol: on each new WS connection the server emits a one-shot "welcome"
// frame ~50ms after accept carrying its protocol_version, node, features,
// warp_active, and version strings. Clients wait up to 1s for it before
// sending the first resolve; absent welcome → assume v1 server. Once welcome
// confirms v2, ResolveAsync stamps protocol_version=2 on outgoing requests
// (unless the patched yt-dlp already set it) so the server emits v2 response
// fields (container, video_codec, audio_codec, protocol, audio_channels,
// bytes_estimate, expires_at).
//
// Public surface: ResolveAsync takes the WHOLE ResolveRequest DTO so unknown
// fields (and v2 fields the patched yt-dlp populated) round-trip losslessly
// to the server.
internal sealed class MeshClient : IAsyncDisposable
{
    private static readonly Uri ApexUrl = new("https://whyknot.dev/");
    private static readonly TimeSpan ApexAttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PongDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ApexReResolveAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WelcomeTimeout = TimeSpan.FromSeconds(1);
    private static readonly int[] ReconnectCapsSec = { 1, 2, 4, 8, 16, 30 };

    private readonly string _userAgent;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly Random _rng = new();

    private ClientWebSocket? _ws;
    private string? _cachedNodeHost;
    private CancellationTokenSource? _runCts;
    private Task? _runner;
    private DateTime _firstReconnectFailureUtc = DateTime.MinValue;
    private DateTime _lastPongUtc = DateTime.MinValue;
    private int _reconnectAttempt;
    private bool _wasConnected;

    // Per-connection welcome state. _welcomeTcs is reset on every successful
    // ConnectAsync; the 1s fallback completes it with null if the server is
    // pre-v2 (silent) and the dispatch handler completes it with the parsed
    // frame on welcome arrival. _serverProtocolVersion is 0 = pre-welcome,
    // 1 = no welcome arrived (assume v1 server), 2 = v2 confirmed.
    private TaskCompletionSource<WelcomeFrame?>? _welcomeTcs;
    private int _serverProtocolVersion;
    private string? _serverNode;
    private string[]? _serverFeatures;
    private bool? _warpActive;
    private string? _serverVersion;
    private string? _ytDlpVersion;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public int ServerProtocolVersion => _serverProtocolVersion;
    public string? ServerNode => _serverNode;
    public bool? WarpActive => _warpActive;

    public MeshClient()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        _userAgent = "WKVRCProxy-Watchdog/" + ver;
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _httpClient = new HttpClient(handler) { Timeout = ApexAttemptTimeout };
    }

    public Task StartAsync()
    {
        _runCts = new CancellationTokenSource();
        _runner = Task.Run(() => RunLoopAsync(_runCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _runCts?.Cancel();
        FailAllPending(WireConstants.FallbackServerUnreachable);
        try
        {
            if (_ws?.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(2000);
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", cts.Token).ConfigureAwait(false);
            }
        }
        catch { /* best-effort */ }
        if (_runner != null)
        {
            try { await _runner.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    // Lossless forward: caller supplies the full ResolveRequest DTO from the
    // pipe. The watchdog adds protocol_version=2 if the server is v2-capable
    // and the patched yt-dlp didn't already set it; everything else round-trips
    // unchanged via [JsonExtensionData] on the DTO.
    public async Task<JsonDocument> ResolveAsync(ResolveRequest req, CancellationToken ct)
    {
        // H5: defensive against null DTO from a misbehaving caller. Synthesize
        // a fallback rather than NRE before we have an id to key on.
        if (req == null)
            return MakeFallbackDoc("", WireConstants.FallbackInternalError);

        // Generate per-attempt id if patched yt-dlp didn't supply one. Needed
        // for the pending-TCS key regardless.
        if (string.IsNullOrEmpty(req.Id))
            req.Id = Guid.NewGuid().ToString("N");

        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
            return MakeFallbackDoc(req.Id, WireConstants.FallbackServerUnreachable);

        // Per-connection welcome handshake — wait up to 1s so we know whether
        // the server is v2-capable before deciding whether to opt into v2
        // response fields. After the first wait completes (welcome or 1s
        // fallback) the TCS stays completed for the connection's lifetime
        // and subsequent resolves return instantly.
        var welcomeTcs = _welcomeTcs;
        if (welcomeTcs is { Task.IsCompleted: false })
        {
            try { await welcomeTcs.Task.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        // Stamp protocol_version=2 ONLY when the server is v2-capable AND the
        // patched yt-dlp has signalled awareness of v2 (either by setting
        // protocol_version itself, or populating any optional v2 request
        // field). Pre-fix this auto-stamped on any v1-shape request — pushing
        // v2 response fields onto a strict-shape v1 patched yt-dlp that never
        // opted in. Now: lossless v1 in → v1 out for callers that haven't
        // declared v2 awareness.
        if (_serverProtocolVersion >= 2 && !req.ProtocolVersion.HasValue && CallerOptedIntoV2(req))
            req.ProtocolVersion = WireConstants.ClientProtocolVersion;

        var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[req.Id] = tcs;

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(req);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(req.Id, out _);
            Console.WriteLine($"[mesh] request serialization failed id={req.Id}: {ex.Message}");
            return MakeFallbackDoc(req.Id, WireConstants.FallbackInternalError);
        }

        try
        {
            await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(req.Id, out _);
            Console.WriteLine(
                "[mesh] send failed id=" + req.Id +
                CidSuffix(req.CorrelationId) +
                ": " + ex.GetType().Name + ": " +
                LogUtil.SanitizeForConsole(ex.Message, 160));
            return MakeFallbackDoc(req.Id, WireConstants.FallbackServerUnreachable);
        }

        string id = req.Id;
        await using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetCanceled();
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return MakeFallbackDoc(id, WireConstants.FallbackServerUnreachable);
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string node = await ResolveNodeHostAsync(ct).ConfigureAwait(false);

                // Prepare per-connection welcome state BEFORE ConnectAsync. By the
                // time _ws.State becomes Open (ConnectAsync returns), _welcomeTcs
                // is guaranteed non-null — closing the race window where a
                // ResolveAsync caller could observe an Open socket but a stale
                // (or null) TCS and skip the welcome wait.
                _serverProtocolVersion = 0;
                _serverNode = null;
                _serverFeatures = null;
                _warpActive = null;
                _serverVersion = null;
                _ytDlpVersion = null;
                var welcomeTcs = new TaskCompletionSource<WelcomeFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _welcomeTcs = welcomeTcs;

                _ws = new ClientWebSocket();
                _ws.Options.SetRequestHeader("User-Agent", _userAgent);
                var wsUri = new Uri("wss://" + node + "/mesh");
                await _ws.ConnectAsync(wsUri, ct).ConfigureAwait(false);

                _cachedNodeHost = node;
                _wasConnected = true;
                _firstReconnectFailureUtc = DateTime.MinValue;
                _lastPongUtc = DateTime.UtcNow;

                // 1s welcome fallback. The continuation gates on
                // `_welcomeTcs == welcomeTcs` so a stale timer from an earlier
                // connection that fires after a fast reconnect can't clobber the
                // new connection's _serverProtocolVersion.
                _ = Task.Delay(WelcomeTimeout, ct).ContinueWith(_ =>
                {
                    if (_welcomeTcs != welcomeTcs) return; // a newer connection took over
                    if (welcomeTcs.TrySetResult(null))
                    {
                        Interlocked.CompareExchange(ref _serverProtocolVersion, 1, 0);
                        Console.WriteLine("[mesh] no welcome within 1s — assuming v1 server");
                    }
                }, TaskScheduler.Default);

                Console.WriteLine("[mesh] connected node=" + node);

                await PumpAsync(ct).ConfigureAwait(false);
                Console.WriteLine("[mesh] disconnected — clean close");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_wasConnected) Console.WriteLine("[mesh] disconnected — " + ex.Message);
                else if (_reconnectAttempt == 0) Console.WriteLine("[mesh] disconnected — " + ex.Message);
                _wasConnected = false;
                FailAllPending(WireConstants.FallbackServerUnreachable);
                if (_firstReconnectFailureUtc == DateTime.MinValue)
                    _firstReconnectFailureUtc = DateTime.UtcNow;
                if (DateTime.UtcNow - _firstReconnectFailureUtc > ApexReResolveAfter)
                    _cachedNodeHost = null;
            }
            finally
            {
                // Drain any blocked welcome waiters so subsequent ResolveAsync
                // calls don't hang on a stale TCS while we try to reconnect.
                _welcomeTcs?.TrySetResult(null);
                try { _ws?.Dispose(); } catch { /* ignore */ }
                _ws = null;
            }

            if (ct.IsCancellationRequested) break;

            _reconnectAttempt++;
            int capSec = ReconnectCapsSec[Math.Min(_reconnectAttempt - 1, ReconnectCapsSec.Length - 1)];
            int waitMs = _rng.Next(0, capSec * 1000 + 1);
            Console.WriteLine($"[mesh] reconnect attempt {_reconnectAttempt} in {waitMs / 1000} s");
            try { await Task.Delay(waitMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<string> ResolveNodeHostAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedNodeHost)) return _cachedNodeHost!;

        Exception? lastEx = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApexUrl);
                req.Headers.UserAgent.ParseAdd(_userAgent);
                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400 && resp.Headers.Location != null)
                {
                    // Resolve relative redirects against the apex base. A
                    // bare path like "/foo" or "node3.whyknot.dev" was
                    // previously fed straight into wss://"<path>"/mesh and
                    // produced a malformed URI that threw on every
                    // reconnect — a permanent storm. Now: convert to an
                    // absolute Uri (combining apex + Location) and pull
                    // the Host. Also reject Locations whose Host equals
                    // the apex host (302 → apex loop).
                    var loc = resp.Headers.Location;
                    var abs = loc.IsAbsoluteUri ? loc : new Uri(ApexUrl, loc);
                    string host = abs.Host;
                    if (!string.IsNullOrEmpty(host)
                        && !host.Equals(ApexUrl.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        return host;
                    }
                }
                lastEx = new InvalidOperationException("apex returned " + (int)resp.StatusCode + " with no usable Location");
            }
            catch (Exception ex) { lastEx = ex; }
            try { await Task.Delay(1000 * attempt, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        FailAllPending(WireConstants.FallbackServerUnreachable);
        Console.WriteLine("[mesh] apex discovery failed — falling back to native yt-dlp until reconnect succeeds.");
        throw lastEx ?? new InvalidOperationException("apex discovery failed");
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task hbTask = HeartbeatLoopAsync(pumpCts.Token);

        try
        {
            var buf = new Memory<byte>(new byte[64 * 1024]);
            using var ms = new MemoryStream();
            while (!pumpCts.IsCancellationRequested)
            {
                ms.SetLength(0);
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await _ws!.ReceiveAsync(buf, pumpCts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf.Span[..r.Count]);
                    if (ms.Length > 4 * 1024 * 1024) throw new InvalidOperationException("frame too large");
                } while (!r.EndOfMessage);

                if (r.MessageType != WebSocketMessageType.Text) continue;
                await DispatchFrameAsync(ms.ToArray(), pumpCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            pumpCts.Cancel();
            try { await hbTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // Snapshot _ws to a local so a concurrent run-loop teardown that
            // sets _ws = null can't NRE us between the State check and the
            // SendAsync call.
            var ws = _ws;
            if (ws is not { State: WebSocketState.Open }) return;
            DateTime sentAt = DateTime.UtcNow;
            try
            {
                var ping = JsonSerializer.SerializeToUtf8Bytes(new { action = WireConstants.ActionPing });
                await ws.SendAsync(ping, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch { return; }

            try { await Task.Delay(PongDeadline, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (_lastPongUtc < sentAt)
            {
                try { ws.Abort(); } catch { /* ignore */ }
                return;
            }
        }
    }

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
                string id = "";
                if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    id = idEl.GetString() ?? "";
                if (string.IsNullOrEmpty(id)) { doc.Dispose(); return; }

                if (action == WireConstants.ActionFallbackNative)
                    LogFallbackNative(doc.RootElement, id);

                if (_pending.TryRemove(id, out var tcs))
                {
                    if (action == WireConstants.ActionResolved) _reconnectAttempt = 0;
                    if (!tcs.TrySetResult(doc))
                    {
                        // Caller already cancelled — own the disposal so we
                        // don't leak the JsonDocument's pooled buffers.
                        doc.Dispose();
                    }
                }
                else
                {
                    doc.Dispose();
                }
                return;
            }
            case WireConstants.ActionWelcome:
            {
                WelcomeFrame? welcome = null;
                try { welcome = JsonSerializer.Deserialize<WelcomeFrame>(payload); }
                catch (Exception ex)
                {
                    Console.WriteLine("[mesh] welcome parse failed — assuming v1 server: "
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

                    int featureCount = welcome.Features?.Length ?? 0;
                    Console.WriteLine(
                        "[mesh] welcome node=" + (welcome.Node ?? "?") +
                        " v=" + welcome.ProtocolVersion + " (negotiated=" + negotiated + ")" +
                        " server=" + (welcome.ServerVersion ?? "?") +
                        " yt-dlp=" + (welcome.YtDlpVersion ?? "?") +
                        " warp_active=" + (welcome.WarpActive?.ToString() ?? "?") +
                        " features=" + featureCount);
                }
                _welcomeTcs?.TrySetResult(welcome);
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
                        var pong = JsonSerializer.SerializeToUtf8Bytes(new { action = WireConstants.ActionPong });
                        await pongWs.SendAsync(pong, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                    }
                }
                catch { /* heartbeat will catch dead socket */ }
                return;
            default:
                // Server-supplied string — strip control chars + truncate so a
                // hostile or buggy server can't inject ANSI escapes into the
                // user's console window.
                Console.WriteLine("[mesh] unknown action — discarding: "
                    + LogUtil.SanitizeForConsole(action, 64));
                doc.Dispose();
                return;
        }
    }

    // Surface server-emitted resolve_log frames. The server emits diagnostic
    // narrative per resolve attempt (candidate URLs, codec choices, fallback
    // decisions). Previously discarded silently — now visible on the console
    // so a user debugging "why did this fall back" gets the server's own story.
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

        Console.WriteLine(
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
            Console.WriteLine(
                "[mesh] frame parse failed (" + key + " x" + count + " in last min): " +
                LogUtil.SanitizeForConsole(ex.Message, 80) +
                " — preview=" + LogUtil.PayloadPreview(payload, 120));
        }
    }

    // Format helper: " cid=<id>" suffix for log lines, only when correlation_id
    // is populated. Keeps log-line construction terse at call sites.
    private static string CidSuffix(string? correlationId) =>
        string.IsNullOrEmpty(correlationId) ? "" : " cid=" + LogUtil.SanitizeForConsole(correlationId, 64);

    // Detect whether the patched yt-dlp populated any v2 request field. Used
    // to decide whether the watchdog should auto-stamp protocol_version=2 —
    // the audit's BC1 finding flagged that a strict-shape v1 patched yt-dlp
    // shouldn't suddenly start receiving v2 response fields it never opted
    // into.
    private static bool CallerOptedIntoV2(ResolveRequest req) =>
        req.ProtocolVersion.HasValue ||
        !string.IsNullOrEmpty(req.CorrelationId) ||
        req.AcceptProtocols != null ||
        req.AcceptCodecs != null ||
        req.MaxAudioChannels.HasValue ||
        !string.IsNullOrEmpty(req.VrchatFormatArg);

    // Surface fallback_native reasons on the watchdog console so a user with
    // a console window open can correlate "video failed" with "server bailed
    // because warp_down" or "unity_unsupported_format". The patched yt-dlp
    // still owns the decision of whether to exec yt-dlp-og.exe; the new v2
    // reasons are advisory ("don't bother with native, it'll hit the same
    // wall") but the watchdog enforces nothing.
    private static void LogFallbackNative(JsonElement root, string id)
    {
        string reason = "";
        if (root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
            reason = reasonEl.GetString() ?? "";

        // Sanitize: reason is server-supplied; treat with the same control-char
        // hygiene as `action`.
        reason = LogUtil.SanitizeForConsole(reason, 64);

        switch (reason)
        {
            case WireConstants.ReasonUnityUnsupportedFormat:
                Console.WriteLine($"[mesh] fallback_native id={id} reason=unity_unsupported_format (no Unity-playable stream — try AVPro)");
                return;
            case WireConstants.ReasonWarpDown:
                Console.WriteLine($"[mesh] fallback_native id={id} reason=warp_down (server WARP egress unhealthy — transient, retry shortly or another node)");
                return;
            default:
                Console.WriteLine($"[mesh] fallback_native id={id} reason={(string.IsNullOrEmpty(reason) ? "?" : reason)}");
                return;
        }
    }

    private void FailAllPending(string reason)
    {
        var failedIds = new List<string>();
        foreach (var kvp in _pending.ToArray())
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                failedIds.Add(kvp.Key);
                try
                {
                    var doc = MakeFallbackDoc(kvp.Key, reason);
                    if (!tcs.TrySetResult(doc)) doc.Dispose();
                }
                catch { tcs.TrySetCanceled(); }
            }
        }

        if (failedIds.Count == 0) return;
        // Per-id visibility for post-hoc correlation. Truncate the id list at
        // a reasonable size so a one-off disaster (50+ pending) doesn't fill
        // the scrollback in one line.
        const int MaxIdsInLine = 8;
        string idList = failedIds.Count <= MaxIdsInLine
            ? string.Join(",", failedIds)
            : string.Join(",", failedIds.GetRange(0, MaxIdsInLine)) + ",…(+" + (failedIds.Count - MaxIdsInLine) + ")";
        Console.WriteLine(
            "[mesh] failing " + failedIds.Count + " pending requests reason=" + reason +
            " ids=" + idList);
    }

    private static JsonDocument MakeFallbackDoc(string id, string reason)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = WireConstants.ActionFallbackNative,
            id,
            reason
        });
        return JsonDocument.Parse(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _httpClient.Dispose();
        _runCts?.Dispose();
    }
}
