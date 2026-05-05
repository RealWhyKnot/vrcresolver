using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using MessagePack;
using MessagePack.Resolvers;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Carries the verified raw JSON bytes from a `resolved` / `fallback_native`
// WS frame plus the action + server-supplied reason already extracted by
// the dispatch handler. Letting the caller (LocalIpcServer) write Frame
// straight through to the pipe avoids a JsonDocument re-encode on the hot
// path; passing Action/Reason through avoids a second TryGetProperty parse
// for the user-facing console summary.
internal readonly record struct MeshResolveResult(byte[] Frame, string Action, string? Reason);

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

    // Pre-baked control frames. Both are pure-static -- `{"action":"ping"}`
    // / `{"action":"pong"}` -- so the byte[] can be cached at class-load
    // and reused for every send. Pre-fix each heartbeat / pong-reply
    // allocated a fresh anonymous-object DTO + ArrayPool buffer.
    //
    // AOT migration: anonymous types can't be source-genned (no class
    // declaration to attach [JsonSerializable] to), so the original
    // `JsonSerializer.SerializeToUtf8Bytes(new { action = "ping" })`
    // would fall back to reflection at class-load -- which under AOT
    // throws PlatformNotSupportedException and crashes the watchdog
    // before Main runs. Replaced with UTF-8 string literals that
    // produce identical wire bytes (verified byte-exact by the existing
    // wire-protocol tests).
    private static readonly byte[] PingFrame = "{\"action\":\"ping\"}"u8.ToArray();
    private static readonly byte[] PongFrame = "{\"action\":\"pong\"}"u8.ToArray();

    // AOT-clean MessagePack options. CompositeResolver chains the
    // source-gen resolver (knows our [MessagePackObject] types) and
    // BuiltinResolver (knows primitives + System.String etc.). NOT
    // StandardResolver: its static reference to DynamicObjectResolver +
    // DynamicGenericResolver pulls Reflection.Emit code paths that
    // throw PlatformNotSupportedException under AOT.
    //
    // Probe-validated end-to-end: see project_v3_1_msgpack_client.md.
    // Cached static so each Deserialize call in DispatchBinaryFrameAsync
    // doesn't reconstruct the resolver chain.
    private static readonly MessagePackSerializerOptions s_msgpackOpts =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                MeshMsgpackResolver.Instance,
                BuiltinResolver.Instance));

    private readonly string _userAgent;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MeshResolveResult>> _pending = new();
    private readonly Random _rng = new();

    // Stable per-install identity included on every playback_feedback frame
    // and v3 client_hello as `client_id`. Server logs it as
    // `reported_client_id` (the server tags its own connection-side id
    // separately) so an operator can join WKVRCProxy watchdog logs with
    // server-side failures.jsonl entries without having to match on socket
    // address — and a returning watchdog presents the same identity across
    // launches. Persisted at %LOCALAPPDATA%Low\WKVRCProxy\client_id.txt;
    // see ClientIdentity for the load/create flow.
    private readonly string _clientId = ClientIdentity.LoadOrCreate();

    // Recent (resolved-url → correlation_id) mapping populated when the server
    // returns a `resolved` frame. VrcLogMonitor consults this when emitting
    // playback_feedback so the server's dispatcher can hit its correlation
    // cache (TTL 1h) and attribute the failure to the exact (domain, config)
    // pair instead of falling back to URL-host extraction — which it skips
    // entirely when the host is whyknot.dev (the proxy URL we returned).
    private readonly object _recentCidsLock = new();
    private readonly Dictionary<string, (string Cid, DateTime At)> _recentCids = new();
    private const int MaxRecentCids = 256;
    private static readonly TimeSpan RecentCidsTtl = TimeSpan.FromHours(1);

    // In-flight (request_id → cid) so the resolved-frame handler can lift the
    // cid out of the originating request and stash it under the resolved URL.
    // Patched yt-dlp populates correlation_id when it knows; otherwise we use
    // the request's `id` so the server can still cache-key on something stable.
    private readonly ConcurrentDictionary<string, string> _inflightCids = new();

    private ClientWebSocket? _ws;
    private string? _cachedNodeHost;
    private CancellationTokenSource? _runCts;
    private Task? _runner;
    private DateTime _firstReconnectFailureUtc = DateTime.MinValue;
    private DateTime _lastPongUtc = DateTime.MinValue;
    private int _reconnectAttempt;
    private bool _wasConnected;

    // v3 handshake state (per-connection). _isV3Connection is set on
    // ConnectAsync return based on the server's echoed subprotocol; if
    // it doesn't come back as "whyknot-v3" we fall back to the v2 path
    // (skip client_hello, wait for plain welcome). _currentNodeHost is
    // captured per-connection so the welcome-cache lookup keys on the
    // exact node we connected to (apex-302 routes to either node1 or
    // node2 and their welcomes can differ).
    private bool _isV3Connection;
    private string _currentNodeHost = "";
    private readonly WelcomeCache _welcomeCache = new();

    // v3.1: post-welcome wire format the server selected for THIS
    // connection. Set on welcome / welcome_cached receipt from the
    // negotiated_format field; null/missing field defaults to
    // FormatJson (v3.0 behaviour). Drives the receive-loop branch on
    // WebSocketMessageType.Binary — only honoured when this is "msgpack".
    private string _negotiatedFormat = WireConstants.FormatJson;
    private bool _isMsgpackFormat;

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
    // Hostname of the WS endpoint we're currently connected to (e.g.
    // "node1.whyknot.dev"). Used by ResolveCache as part of the cache
    // key so different mesh nodes never cross-serve cached URLs.
    // Distinct from ServerNode -- ServerNode is the server-supplied
    // logical node label from the welcome frame; CurrentNodeHost is
    // the DNS hostname we resolved to.
    public string CurrentNodeHost => _currentNodeHost;
    public bool? WarpActive => _warpActive;

    // Fire-and-forget client → server feedback frame. Sent when VrcLogMonitor
    // observes that AVPro couldn't actually play a URL the dispatcher
    // returned (load_failure within 10 s of Opening, or silent_stall after
    // 12 s of nothing). Drops silently if the WS is down — the next launch
    // re-reads the current output_log_*.txt and reports any unsignalled
    // failures it finds there.
    //
    // Feature-gated on welcome.features containing "playback_feedback" so an
    // older server (before 2026.5.4.0-0AFF) doesn't see an unknown action.
    public async Task SendPlaybackFeedbackAsync(string url, string kind, int msSinceOpen)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(kind)) return;

        var features = _serverFeatures;
        if (features == null || Array.IndexOf(features, WireConstants.ActionPlaybackFeedback) < 0)
            return;

        string? cid = LookupRecentCorrelationId(url);

        byte[] payload;
        try
        {
            payload = BuildPlaybackFeedbackPayload(
                url, kind, msSinceOpen, _clientId, cid, DateTime.UtcNow);
        }
        catch { return; }

        try
        {
            await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* best-effort — heartbeat/run-loop will catch a dead socket */ }
    }

    // Pure predicate so the subprotocol-mismatch fallback can be unit-
    // tested without standing up a real ClientWebSocket. True iff the
    // server echoed the v3 subprotocol literal back on the upgrade —
    // null/empty/anything-else means the server (or an intermediate
    // proxy stripping unrecognized headers, e.g. some Cloudflare
    // configs) didn't accept v3, and the client must fall back to the
    // v2 path: skip client_hello, wait for plain welcome.
    internal static bool ShouldSendClientHello(string? negotiatedSubprotocol)
        => string.Equals(negotiatedSubprotocol, WireConstants.SubprotocolV3, StringComparison.Ordinal);

    // Send the v3 first frame. Looks up any cached welcome hash for the
    // current node so the server can reply with a small welcome_cached
    // frame on a match instead of resending the full welcome bytes.
    private async Task SendClientHelloAsync(string nodeHost, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: System.Net.WebSockets.WebSocketState.Open }) return;
        string? cachedHash = _welcomeCache.Get(nodeHost)?.WelcomeHash;
        var hello = new ClientHelloFrame
        {
            WelcomeHash = cachedHash,
            ClientId = _clientId,
            // v3.1: prefer msgpack on the post-welcome hot path,
            // fall back to json. Server picks the first format from
            // this list that it supports — v3.0 servers (or v3.1
            // servers that fail to advertise msgpack_format) just
            // pick "json" and the connection runs as v3.0. Binary
            // frame dispatch is gated on _isMsgpackFormat, set on
            // welcome / welcome_cached receipt from the
            // negotiated_format field.
            AcceptFormats = WireConstants.AcceptFormatsPreference,
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(hello, MeshJsonContext.Default.ClientHelloFrame);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        Logger.WriteFileOnly("[mesh][v3] client_hello sent node=" + nodeHost
            + " hash=" + (cachedHash ?? "null"));
    }

    // Frame builder split out so the wire shape can be unit-tested without
    // standing up a real MeshClient + WS. `correlation_id` is omitted (not
    // serialized as null) when caller passes null — keeps the frame shape
    // consistent with "missing field" semantics on the server.
    internal static byte[] BuildPlaybackFeedbackPayload(
        string url,
        string kind,
        int msSinceOpen,
        string clientId,
        string? correlationId,
        DateTime timestampUtc)
    {
        // AOT migration: Dictionary<string, object?> + reflection-based
        // SerializeToUtf8Bytes replaced with the typed PlaybackFeedbackFrame
        // DTO routed through MeshJsonContext source-gen. Wire shape
        // preserved byte-exact -- correlation_id still omitted (not
        // serialized as null) when caller passes null, matched by
        // [JsonIgnore(Condition = WhenWritingNull)] on the property.
        var frame = new PlaybackFeedbackFrame
        {
            Url = url,
            Kind = kind,
            Timestamp = timestampUtc.ToString("o"),
            MsSinceOpen = msSinceOpen,
            ClientId = clientId,
            CorrelationId = string.IsNullOrEmpty(correlationId) ? null : correlationId,
        };
        return JsonSerializer.SerializeToUtf8Bytes(frame, MeshJsonContext.Default.PlaybackFeedbackFrame);
    }

    private string? LookupRecentCorrelationId(string url)
    {
        lock (_recentCidsLock)
        {
            if (!_recentCids.TryGetValue(url, out var entry))
                return null;
            if (DateTime.UtcNow - entry.At > RecentCidsTtl)
            {
                _recentCids.Remove(url);
                return null;
            }
            return entry.Cid;
        }
    }

    private void RememberResolvedUrlCid(string url, string cid)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(cid)) return;
        lock (_recentCidsLock)
        {
            _recentCids[url] = (cid, DateTime.UtcNow);
            if (_recentCids.Count <= MaxRecentCids) return;

            // Cap by evicting the oldest. 256 entries × occasional eviction is
            // cheap enough; no need for a proper LRU structure.
            string? oldestKey = null;
            DateTime oldestAt = DateTime.MaxValue;
            foreach (var kvp in _recentCids)
            {
                if (kvp.Value.At < oldestAt) { oldestAt = kvp.Value.At; oldestKey = kvp.Key; }
            }
            if (oldestKey != null) _recentCids.Remove(oldestKey);
        }
    }

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
    //
    // Returns a MeshResolveResult carrying the verified raw response bytes
    // PLUS the parsed action and server-supplied reason. The caller writes
    // Frame straight to the pipe — no JsonDocument re-encode on the hot path.
    public async Task<MeshResolveResult> ResolveAsync(ResolveRequest req, CancellationToken ct)
    {
        // H5: defensive against null DTO from a misbehaving caller. Synthesize
        // a fallback rather than NRE before we have an id to key on.
        if (req == null)
            return MakeFallbackResult("", WireConstants.FallbackInternalError);

        // Generate per-attempt id if patched yt-dlp didn't supply one. Needed
        // for the pending-TCS key regardless.
        if (string.IsNullOrEmpty(req.Id))
            req.Id = Guid.NewGuid().ToString("N");

        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
            return MakeFallbackResult(req.Id, WireConstants.FallbackServerUnreachable);

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

        // Stamp protocol_version=ClientProtocolVersion (currently 3) ONLY
        // when the server is v2-or-newer AND the patched yt-dlp has
        // signalled awareness of v2+ (either by setting protocol_version
        // itself, or populating any optional v2 request field). Pre-fix
        // this auto-stamped on any v1-shape request — pushing v2 response
        // fields onto a strict-shape v1 patched yt-dlp that never opted
        // in. Now: lossless v1 in → v1 out for callers that haven't
        // declared v2 awareness.
        //
        // Wire note: a v1-shape wrapper short-circuits CallerOptedIntoV2
        // and req.ProtocolVersion stays null. The default-options
        // JsonSerializer still emits `"protocol_version": null` on the
        // wire (no JsonIgnoreCondition.WhenWritingNull); server must
        // coalesce null → v1 — same behaviour since v2 first shipped.
        if (_serverProtocolVersion >= 2 && !req.ProtocolVersion.HasValue && CallerOptedIntoV2(req))
            req.ProtocolVersion = WireConstants.ClientProtocolVersion;

        var tcs = new TaskCompletionSource<MeshResolveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[req.Id] = tcs;
        // Stash whichever cid the originating request carries (prefer
        // patched-yt-dlp-supplied correlation_id, else fall back to the
        // request id) so the resolved-frame handler can attach it to the
        // (url → cid) recent-resolves map for VrcLogMonitor.
        _inflightCids[req.Id] = string.IsNullOrEmpty(req.CorrelationId) ? req.Id : req.CorrelationId!;

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(req, MeshJsonContext.Default.ResolveRequest);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(req.Id, out _);
            _inflightCids.TryRemove(req.Id, out _);
            Console.WriteLine($"[mesh][warn] request serialization failed id={req.Id}: {ex.Message}");
            return MakeFallbackResult(req.Id, WireConstants.FallbackInternalError);
        }

        try
        {
            await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(req.Id, out _);
            _inflightCids.TryRemove(req.Id, out _);
            Console.WriteLine(
                "[mesh][warn] send failed id=" + req.Id +
                CidSuffix(req.CorrelationId) +
                ": " + ex.GetType().Name + ": " +
                LogUtil.SanitizeForConsole(ex.Message, 160));
            return MakeFallbackResult(req.Id, WireConstants.FallbackServerUnreachable);
        }

        string id = req.Id;
        await using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetCanceled();
            _inflightCids.TryRemove(id, out _);
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return MakeFallbackResult(id, WireConstants.FallbackServerUnreachable);
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
                _isV3Connection = false;
                _negotiatedFormat = WireConstants.FormatJson;
                _isMsgpackFormat = false;
                _currentNodeHost = node;
                var welcomeTcs = new TaskCompletionSource<WelcomeFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _welcomeTcs = welcomeTcs;

                _ws = new ClientWebSocket();
                _ws.Options.SetRequestHeader("User-Agent", _userAgent);
                // We implement our own 45 s heartbeat loop; turn off the
                // framework's redundant 30 s background ping.
                _ws.Options.KeepAliveInterval = TimeSpan.Zero;
                // v3: offer the whyknot-v3 subprotocol on the upgrade. If
                // the server (or an intermediate proxy) doesn't echo it
                // back, _ws.SubProtocol will be null/different and we
                // silently fall back to v2 behaviour below.
                _ws.Options.AddSubProtocol(WireConstants.SubprotocolV3);
                // v3: offer permessage-deflate (RFC 7692) compression on
                // the upgrade. Server still gets to refuse, in which case
                // the connection is uncompressed but functional — same
                // wire shape, just larger frames.
                //
                // Microsoft named the property "Dangerous" because of
                // DECOMPRESSION-AMPLIFICATION risk: a hostile peer can
                // send a small compressed payload that decompresses to
                // arbitrarily many bytes (a "zip bomb" on the wire). With
                // an unbounded receiver, that's an OOM vector.
                //
                // Trust boundary: this MeshClient ONLY connects to
                // wss://*.whyknot.dev/mesh (the apex-redirect at line
                // ~437 below resolves to one of node1/node2). We trust
                // that endpoint not to send hostile decompressed
                // payloads. If MeshClient is ever pointed at a different
                // endpoint (e.g., a self-hosted mesh tier, a
                // user-configurable URL), this trust assumption breaks
                // and DangerousDeflateOptions must be revisited.
                //
                // Mitigation in depth: the WS receive loop at line ~592
                // caps any single received frame (post-decompression) at
                // 4 MiB and tears down the connection on overflow. Even
                // if the trust assumption fails, the blast radius is one
                // 4 MiB alloc per connection plus reconnect-with-backoff
                // (capped 30 s).
                _ws.Options.DangerousDeflateOptions = new System.Net.WebSockets.WebSocketDeflateOptions
                {
                    ClientMaxWindowBits = 15,
                    ServerMaxWindowBits = 15,
                };
                var wsUri = new Uri("wss://" + node + "/mesh");
                await _ws.ConnectAsync(wsUri, ct).ConfigureAwait(false);

                _isV3Connection = ShouldSendClientHello(_ws.SubProtocol);
                Logger.WriteFileOnly("[mesh][v3] negotiated subprotocol="
                    + (_ws.SubProtocol ?? "<none>")
                    + " v3=" + _isV3Connection
                    + " deflate-offered=true");

                // v3: send client_hello as the FIRST outbound frame
                // before any resolve. Fire-and-forget on send failures
                // — the server's response to client_hello (welcome or
                // welcome_cached) flows through the existing dispatch
                // path, and any connection-level error tears down the
                // socket which the run loop's catch handles.
                if (_isV3Connection)
                {
                    try { await SendClientHelloAsync(node, ct).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Logger.WriteFileOnly("[mesh][v3] client_hello send failed: "
                            + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
                        // Don't crash the connection — the server may
                        // still send a plain welcome on its own.
                    }
                }

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
                        // File-only: only fires against pre-v3 servers and is
                        // silent on every modern connection. The console line
                        // was demoted in the logging audit (production whyknot
                        // is v3 and always responds; if this fires it's a
                        // server regression and shows up in the rolling log).
                        Logger.WriteFileOnly("[mesh] no welcome within 1s -- assuming v1 server");
                    }
                }, TaskScheduler.Default);

                Console.WriteLine("[mesh] connected node=" + node);

                await PumpAsync(ct).ConfigureAwait(false);
                Console.WriteLine("[mesh] disconnected (clean close)");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Distinguish abnormal disconnects from clean ones with a
                // [warn] prefix so a user grepping the log can see at a
                // glance that something failed.
                if (_wasConnected) Console.WriteLine("[mesh][warn] disconnected (error): "
                    + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
                else if (_reconnectAttempt == 0) Console.WriteLine("[mesh][warn] disconnected (error): "
                    + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
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
            WatchdogStats.RecordReconnect();
            int capSec = ReconnectCapsSec[Math.Min(_reconnectAttempt - 1, ReconnectCapsSec.Length - 1)];
            int waitMs = _rng.Next(0, capSec * 1000 + 1);
            // Dedupe: log every attempt for the first 5, then every 10th, so a
            // sustained outage doesn't fill the scrollback with retry chatter.
            if (_reconnectAttempt <= 5 || _reconnectAttempt % 10 == 0)
            {
                Console.WriteLine($"[mesh] reconnect attempt {_reconnectAttempt} in {waitMs / 1000} s");
            }
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
        Console.WriteLine("[mesh][warn] apex discovery failed -- falling back to native yt-dlp until reconnect succeeds.");
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

                switch (r.MessageType)
                {
                    case WebSocketMessageType.Text:
                        await DispatchFrameAsync(ms.ToArray(), pumpCts.Token).ConfigureAwait(false);
                        break;
                    case WebSocketMessageType.Binary:
                        if (_isMsgpackFormat)
                        {
                            await DispatchBinaryFrameAsync(ms.ToArray(), pumpCts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            // Defense-in-depth: server should NEVER send
                            // Binary on a connection it negotiated as
                            // json. Either a server bug or a hostile
                            // MITM on the upgraded WS. Tear down + let
                            // the run loop reconnect with a fresh
                            // negotiation.
                            Console.WriteLine("[mesh][warn] unexpected Binary frame on json-negotiated connection -- aborting + reconnecting");
                            try { _ws?.Abort(); } catch { /* ignore */ }
                            return;
                        }
                        break;
                    // Close handled above. Other types (Ping/Pong are
                    // handled by the framework, not surfaced through
                    // ReceiveAsync) ignored.
                }
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
                await ws.SendAsync(PingFrame, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
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

    // v3.1: dispatch a server-sent Binary frame. Decodes the msgpack
    // payload via per-action DTO, transcodes to the JSON shape the
    // wrapper-side pipe expects, and routes through the same _pending
    // TCS map that DispatchFrameAsync's text-frame path uses. Wrapper
    // sees identical JSON regardless of which wire format the watchdog
    // negotiated; only the WS hop changes.
    //
    // Action discriminant lives at [Key(0)]. Peeking it via
    // MessagePackReader avoids a full deserialize of the trailing
    // fields when we can't route the frame anyway. Per-DTO deserialize
    // happens once we know the action.
    //
    // Decoder cold-start: first deserialize per type pays a one-time
    // ~5-20 ms IL emit cost (MessagePackSerializer's runtime resolver
    // builds a formatter on first use, caches forever). Subsequent
    // calls run at near-source-gen speed. Acceptable for the
    // watchdog's hours-long lifetime — see project_v3_1_msgpack_client.md
    // for the source-gen deferral rationale.
    private async Task DispatchBinaryFrameAsync(byte[] payload, CancellationToken ct)
    {
        // Peek action without consuming the array — MessagePackReader
        // is a ref-struct over a ReadOnlySequence<byte> view.
        string action;
        try
        {
            var reader = new MessagePack.MessagePackReader(payload);
            // Frame is a fixed-length array; first element is action string.
            int count = reader.ReadArrayHeader();
            if (count < 1)
            {
                LogBinaryParseFailure("empty msgpack array (count=0)", payload);
                return;
            }
            action = reader.ReadString() ?? "";
        }
        catch (Exception ex)
        {
            LogBinaryParseFailure("action peek: " + ex.GetType().Name + ": " + ex.Message, payload);
            return;
        }

        switch (action)
        {
            case WireConstants.ActionResolved:
            {
                MsgpackResolvedFrame? mp;
                try { mp = MessagePackSerializer.Deserialize<MsgpackResolvedFrame>(payload, s_msgpackOpts); }
                catch (Exception ex)
                {
                    LogBinaryParseFailure("resolved deserialize: " + ex.GetType().Name + ": " + ex.Message, payload);
                    return;
                }
                if (mp == null || string.IsNullOrEmpty(mp.Id)) return;

                // Transcode to the JSON ResolveResponse shape the
                // wrapper expects. Field-by-field copy — server's
                // msgpack tag list omits Reason/Message on resolved
                // frames so we just don't populate them.
                var resp = new ResolveResponse
                {
                    Action = WireConstants.ActionResolved,
                    Id = mp.Id ?? "",
                    Url = mp.Url,
                    Engine = mp.Engine,
                    Config = mp.Config,
                    Container = mp.Container,
                    VideoCodec = mp.VideoCodec,
                    AudioCodec = mp.AudioCodec,
                    Protocol = mp.Protocol,
                    AudioChannels = mp.AudioChannels,
                    BytesEstimate = mp.BytesEstimate,
                    ExpiresAt = mp.ExpiresAt,
                };
                byte[] jsonFrame = JsonSerializer.SerializeToUtf8Bytes(resp, MeshJsonContext.Default.ResolveResponse);

                // Same downstream routing as the text-path: stats
                // recording (bytes_estimate accumulator), recent-cid
                // map (resolved URL → cid for VrcLogMonitor), pending
                // TCS resolve.
                if (mp.BytesEstimate.HasValue)
                    WatchdogStats.RecordBytesEstimate(mp.BytesEstimate.Value);

                _inflightCids.TryRemove(mp.Id!, out var cid);
                if (!string.IsNullOrEmpty(cid) && !string.IsNullOrEmpty(mp.Url))
                    RememberResolvedUrlCid(mp.Url!, cid);

                if (_pending.TryRemove(mp.Id!, out var tcs))
                {
                    _reconnectAttempt = 0;
                    tcs.TrySetResult(new MeshResolveResult(jsonFrame, WireConstants.ActionResolved, null));
                }
                return;
            }
            case WireConstants.ActionFallbackNative:
            {
                MsgpackFallbackNativeFrame? mp;
                try { mp = MessagePackSerializer.Deserialize<MsgpackFallbackNativeFrame>(payload, s_msgpackOpts); }
                catch (Exception ex)
                {
                    LogBinaryParseFailure("fallback_native deserialize: " + ex.GetType().Name + ": " + ex.Message, payload);
                    return;
                }
                if (mp == null || string.IsNullOrEmpty(mp.Id)) return;

                LogFallbackNative(mp.Id!, mp.Reason);

                var resp = new ResolveResponse
                {
                    Action = WireConstants.ActionFallbackNative,
                    Id = mp.Id ?? "",
                    Reason = mp.Reason,
                };
                byte[] jsonFrame = JsonSerializer.SerializeToUtf8Bytes(resp, MeshJsonContext.Default.ResolveResponse);

                _inflightCids.TryRemove(mp.Id!, out _);
                if (_pending.TryRemove(mp.Id!, out var tcs))
                {
                    tcs.TrySetResult(new MeshResolveResult(jsonFrame, WireConstants.ActionFallbackNative, mp.Reason));
                }
                return;
            }
            case WireConstants.ActionResolveLog:
            {
                MsgpackResolveLogFrame? mp;
                try { mp = MessagePackSerializer.Deserialize<MsgpackResolveLogFrame>(payload, s_msgpackOpts); }
                catch (Exception ex)
                {
                    LogBinaryParseFailure("resolve_log deserialize: " + ex.GetType().Name + ": " + ex.Message, payload);
                    return;
                }
                if (mp == null) return;
                Logger.WriteFileOnly(
                    "[mesh][resolve_log] id=" + LogUtil.SanitizeForConsole(mp.Id ?? "", 32) +
                    " " + LogUtil.SanitizeForConsole(mp.Message ?? "", 240));
                return;
            }
            // Defense-in-depth (2026-05-05 incident): per the v3.1 spec, control
            // frames (pong / protocol_error / rate_limited) ALWAYS go as JSON-Text,
            // but a server-side regression at commit 2c4b432 (since fixed) routed
            // pong through SendTo<T>, sending it as msgpack-Binary on negotiated
            // connections. The client's binary dispatch had only the three
            // hot-path actions and default-discarded pong -- _lastPongUtc never
            // advanced, heartbeat watchdog aborted the WS at every PongDeadline,
            // and the user got a ~55 s reconnect storm for hours.
            //
            // Now we tolerate control actions on either path so a future server
            // regression can't reproduce the storm. We don't bother decoding the
            // body (pong has no payload; protocol_error/rate_limited have fields
            // but no DTOs over here for the binary shape -- their content stays
            // file-only diagnostic).
            case WireConstants.ActionPong:
                _lastPongUtc = DateTime.UtcNow;
                Logger.WriteFileOnly("[mesh] pong received via binary path (server should send as Text per v3.1 spec)");
                return;
            case WireConstants.ActionPing:
            {
                Logger.WriteFileOnly("[mesh] ping received via binary path (server should send as Text per v3.1 spec)");
                try
                {
                    var pongWs = _ws;
                    if (pongWs is { State: WebSocketState.Open })
                    {
                        await pongWs.SendAsync(PongFrame, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                    }
                }
                catch { /* heartbeat will catch dead socket */ }
                return;
            }
            case "protocol_error":
            case "rate_limited":
                Console.WriteLine("[mesh][warn] " + action + " received via binary path "
                    + "(server should send as Text per v3.1 spec)");
                return;
            default:
                Console.WriteLine("[mesh][warn] unknown binary action -- discarding: "
                    + LogUtil.SanitizeForConsole(action, 64));
                return;
        }
    }

    // Dedupe binary parse failures by message prefix so a flapping
    // server can't flood scrollback. One log per (prefix) per minute.
    // Uses the existing _parseFailDedupe dict scheme keyed on the
    // exception message rather than type name (binary errors come
    // through with rich messages, not type-name distinctions).
    private void LogBinaryParseFailure(string detail, byte[] payload)
    {
        string key = "binary:" + detail.Split(':')[0];
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
                "[mesh][warn] binary frame parse failed (" + key + " x" + count + " in last min): " +
                LogUtil.SanitizeForConsole(detail, 200) +
                " — preview=" + LogUtil.PayloadPreview(payload, 60));
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
                    Console.WriteLine("[mesh][warn] welcome parse failed -- assuming v1 server: "
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
                        Console.WriteLine("[mesh][warn] welcome missing required field: engines");
                    if (welcome.Features == null)
                        Console.WriteLine("[mesh][warn] welcome missing required field: features");

                    string features = welcome.Features != null && welcome.Features.Length > 0
                        ? "[" + string.Join(",", welcome.Features) + "]"
                        : "[]";
                    Console.WriteLine(
                        "[mesh] welcome node=" + (welcome.Node ?? "?") +
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
                    Console.WriteLine("[mesh][warn] welcome_cached received on non-v3 connection -- protocol error, reconnecting");
                    try { _ws?.Abort(); } catch { /* ignore */ }
                    doc.Dispose();
                    return;
                }
                WelcomeCachedFrame? cached = null;
                try { cached = JsonSerializer.Deserialize(payload, MeshJsonContext.Default.WelcomeCachedFrame); }
                catch (Exception ex)
                {
                    Console.WriteLine("[mesh][warn] welcome_cached parse failed: "
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
                    Console.WriteLine("[mesh][warn] welcome_cached but local entry missing -- invalidating + reconnecting");
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
                        await pongWs.SendAsync(PongFrame, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                    }
                }
                catch { /* heartbeat will catch dead socket */ }
                return;
            default:
                // Server-supplied string — strip control chars + truncate so a
                // hostile or buggy server can't inject ANSI escapes into the
                // user's console window.
                Console.WriteLine("[mesh][warn] unknown action -- discarding: "
                    + LogUtil.SanitizeForConsole(action, 64));
                doc.Dispose();
                return;
        }
    }

    // Server-emitted resolve_log frames. The server narrates per-strategy
    // attempts (candidate URLs, codec choices, fallback decisions) — useful
    // for deep diagnosis but verbose enough to drown the user-facing console
    // (5-15 frames per resolve). Routed to FILE-ONLY logging so the watchdog
    // log captures everything for grep / bug reports while the live console
    // stays scannable; LocalIpcServer prints a single user-friendly summary
    // per resolve at terminal-response time.
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
            Console.WriteLine(
                "[mesh][warn] frame parse failed (" + key + " x" + count + " in last min): " +
                LogUtil.SanitizeForConsole(ex.Message, 80) +
                " — preview=" + LogUtil.PayloadPreview(payload, 120));
        }
    }

    // Format helper: " cid=<id>" suffix for log lines, only when correlation_id
    // is populated. Keeps log-line construction terse at call sites.
    private static string CidSuffix(string? correlationId) =>
        string.IsNullOrEmpty(correlationId) ? "" : " cid=" + LogUtil.SanitizeForConsole(correlationId, 64);

    // Detect whether the patched yt-dlp populated any v2 request field. Used
    // to decide whether the watchdog should auto-stamp protocol_version=
    // ClientProtocolVersion (currently 3) — the audit's BC1 finding
    // flagged that a strict-shape v1 patched yt-dlp shouldn't suddenly
    // start receiving v2+ response fields it never opted into. v3 didn't
    // change this gate; the constant being stamped just bumped to 3.
    private static bool CallerOptedIntoV2(ResolveRequest req) =>
        req.ProtocolVersion.HasValue ||
        !string.IsNullOrEmpty(req.CorrelationId) ||
        req.AcceptProtocols != null ||
        req.AcceptCodecs != null ||
        req.MaxAudioChannels.HasValue ||
        !string.IsNullOrEmpty(req.VrchatFormatArg);

    // Server-emitted fallback_native — recorded for grep but no longer
    // surfaced on console. The user-facing per-resolve summary in
    // LocalIpcServer paints the same information as a single coloured
    // "!! fallback (<reason>)" line; this mesh-side trace is redundant
    // there and stayed visible only as legacy verbosity. Routed to the
    // rolling watchdog log so deep diagnosis still has the per-frame
    // record (with the v2-reason advisory copy preserved).
    private static void LogFallbackNative(string id, string? reasonRaw)
    {
        string reason = LogUtil.SanitizeForConsole(reasonRaw ?? "", 64);

        string line = reason switch
        {
            WireConstants.ReasonUnityUnsupportedFormat =>
                $"[mesh] fallback_native id={id} reason=unity_unsupported_format (no Unity-playable stream — try AVPro)",
            WireConstants.ReasonWarpDown =>
                $"[mesh] fallback_native id={id} reason=warp_down (server WARP egress unhealthy — transient, retry shortly or another node)",
            _ =>
                $"[mesh] fallback_native id={id} reason={(string.IsNullOrEmpty(reason) ? "?" : reason)}",
        };
        Logger.WriteFileOnly(line);
    }

    private void FailAllPending(string reason)
    {
        // Clear inflight cids unconditionally — anything still pending now will
        // fail with no `resolved` to redeem the entry. Leaks in the dictionary
        // would slowly grow under sustained reconnect storms.
        _inflightCids.Clear();
        var failedIds = new List<string>();
        foreach (var kvp in _pending.ToArray())
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                failedIds.Add(kvp.Key);
                tcs.TrySetResult(MakeFallbackResult(kvp.Key, reason));
            }
        }

        if (failedIds.Count == 0) return;
        // Per-id visibility for post-hoc correlation. Truncate the id list at
        // a reasonable size so a one-off disaster (50+ pending) doesn't fill
        // the scrollback in one line.
        const int MaxIdsInLine = 8;
        string idList = failedIds.Count <= MaxIdsInLine
            ? string.Join(",", failedIds)
            : string.Join(",", failedIds.GetRange(0, MaxIdsInLine)) + ",...(+" + (failedIds.Count - MaxIdsInLine) + ")";
        Console.WriteLine(
            "[mesh] failing " + failedIds.Count + " pending requests reason=" + reason +
            " ids=" + idList);
    }

    // Synthesize a fallback_native frame (raw JSON bytes + parsed action +
    // reason) for callers that never made it onto the wire — null DTO,
    // socket down, send threw, mesh disconnect during outstanding wait, etc.
    // Bytes match what the server would emit for the same shape so the pipe
    // forward is wire-identical to a real server fallback.
    private static MeshResolveResult MakeFallbackResult(string id, string reason)
    {
        // AOT migration: anonymous-type SerializeToUtf8Bytes replaced
        // with a typed ResolveResponse populated with just the three
        // wire fields the synthetic fallback needs. Routed through the
        // MeshFallbackJsonContext (WhenWritingNull options) so the v2
        // nullable response fields stay omitted on the wire; v1
        // patched-yt-dlp consumers see byte-identical bytes to the
        // pre-migration shape.
        var frame = new ResolveResponse
        {
            Action = WireConstants.ActionFallbackNative,
            Id = id,
            Reason = reason,
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, MeshFallbackJsonContext.Default.ResolveResponse);
        return new MeshResolveResult(bytes, WireConstants.ActionFallbackNative, reason);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        // StopAsync's CloseAsync attempt may have left _ws in a closed-but-
        // not-disposed state if the timeout fired or the close threw. The run
        // loop's finally also disposes _ws on normal exit, but DisposeAsync
        // is the catch-all for "make sure no socket is still pinning a handle."
        try { _ws?.Dispose(); } catch { /* best-effort */ }
        _ws = null;
        _httpClient.Dispose();
        _runCts?.Dispose();
    }
}
