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
internal sealed partial class MeshClient : IAsyncDisposable
{
    private static readonly Uri ApexUrl = new("https://whyknot.dev/");
    private static readonly TimeSpan ApexAttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan HelperStatusRefreshInterval = TimeSpan.FromSeconds(45);
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
    private readonly SemaphoreSlim _sendGate = new(1, 1);

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
    private long _lastHelperStatusRefreshTicks;
    private int _helperStatusRefreshRunning;
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
    public async Task SendPlaybackFeedbackAsync(string url, string kind, int msSinceOpen, int? deliveredHeight = null)
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
                url, kind, msSinceOpen, _clientId, cid, DateTime.UtcNow, deliveredHeight);
        }
        catch { return; }

        try
        {
            await SendTextFrameAsync(payload, CancellationToken.None).ConfigureAwait(false);
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
        await SendTextFrameAsync(bytes, ct).ConfigureAwait(false);
        Logger.WriteFileOnly("[mesh][v3] client_hello sent node=" + nodeHost
            + " hash=" + (cachedHash ?? "null"));
    }

    private void QueueHelperStatusRefresh(bool force = false)
    {
        if (!ServerSupportsFeature(WireConstants.FeatureHelperTranscode))
            return;

        DateTime now = DateTime.UtcNow;
        long lastTicks = Interlocked.Read(ref _lastHelperStatusRefreshTicks);
        if (!force && lastTicks > 0 && now - new DateTime(lastTicks, DateTimeKind.Utc) < HelperStatusRefreshInterval)
            return;
        if (Interlocked.CompareExchange(ref _helperStatusRefreshRunning, 1, 0) != 0)
            return;
        Interlocked.Exchange(ref _lastHelperStatusRefreshTicks, now.Ticks);

        _ = Task.Run(async () =>
        {
            try
            {
                AppSettings settings = AppSettingsStore.Shared.Snapshot();
                FfmpegCapabilityProbeResult probe = await FfmpegCapabilityProbe.ProbeAsync(
                    AppContext.BaseDirectory,
                    FfmpegCapabilityProbe.DefaultTimeout,
                    CancellationToken.None).ConfigureAwait(false);
                HelperEncodingQuality quality = await HelperBenchmarkService.ResolveQualityAsync(
                    settings,
                    probe,
                    CancellationToken.None).ConfigureAwait(false);

                var frame = new HelperStatusFrame
                {
                    ClientId = _clientId,
                    Sharing = settings.Helper.GpuSharing,
                    CanEncodeH264 = settings.Helper.GpuSharing && probe.CanUseHardwareH264,
                    Status = HelperStatusWord(settings, probe),
                    FfmpegVersion = probe.Version?.Version,
                    Encoder = probe.PreferredEncoder?.EncoderName,
                    EncoderBackend = probe.PreferredEncoder?.Backend.ToString().ToLowerInvariant(),
                    GpuLimitPercent = settings.Helper.GpuLimitPercent,
                    UploadLimitMbps = settings.Helper.UploadLimitMbps,
                    AllowOnBattery = settings.Helper.AllowOnBattery,
                };

                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, MeshJsonContext.Default.HelperStatusFrame);
                await SendTextFrameAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                string statusLine = "[mesh][helper] status sent status=" + frame.Status
                    + " encoder=" + (frame.Encoder ?? "<none>")
                    + " quality=" + HelperEncodingQualityNames.Format(quality);
                Logger.WriteDiagnostic(
                    LogComponent.Helper,
                    statusLine,
                    "status sent status=" + frame.Status
                        + " encoder=" + (frame.Encoder ?? "<none>")
                        + " quality=" + HelperEncodingQualityNames.Format(quality));
            }
            catch (Exception ex)
            {
                Logger.WriteFileOnly("[mesh][helper] status send failed: "
                    + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
            }
            finally
            {
                Interlocked.Exchange(ref _helperStatusRefreshRunning, 0);
            }
        });
    }

    private bool ServerSupportsFeature(string feature)
    {
        var features = _serverFeatures;
        return features != null && Array.IndexOf(features, feature) >= 0;
    }

    private static string HelperStatusWord(AppSettings settings, FfmpegCapabilityProbeResult probe)
    {
        if (!settings.Helper.GpuSharing) return "off";
        return probe.Status switch
        {
            FfmpegCapabilityProbeStatus.Ready => "idle",
            FfmpegCapabilityProbeStatus.NotFound => "missing_ffmpeg",
            FfmpegCapabilityProbeStatus.NoHardwareEncoder => "no_encoder",
            FfmpegCapabilityProbeStatus.TimedOut => "probe_timeout",
            FfmpegCapabilityProbeStatus.Failed => "probe_failed",
            _ => "paused",
        };
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
        DateTime timestampUtc,
        int? deliveredHeight = null)
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
            DeliveredHeight = deliveredHeight is > 0 ? deliveredHeight : null,
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
            ConsoleUx.Warn(LogComponent.Mesh, $"request serialization failed id={req.Id}: {ex.Message}");
            return MakeFallbackResult(req.Id, WireConstants.FallbackInternalError);
        }

        try
        {
            await SendTextFrameAsync(payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(req.Id, out _);
            _inflightCids.TryRemove(req.Id, out _);
            ConsoleUx.Warn(
                LogComponent.Mesh,
                "send failed id=" + req.Id +
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

    // Format helper for log lines, only when correlation_id is populated.
    private static string CidSuffix(string? correlationId) =>
        string.IsNullOrEmpty(correlationId) ? "" : " cid=" + LogUtil.SanitizeForConsole(correlationId, 64);

    private async Task SendTextFrameAsync(byte[] payload, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void QueueHelperLease(HelperTranscodeLeaseFrame lease)
    {
        if (lease == null || string.IsNullOrWhiteSpace(lease.LeaseId))
            return;

        Logger.WriteDiagnostic(
            LogComponent.Helper,
            "[mesh][helper] lease queued lease=" + LogUtil.SanitizeForConsole(lease.LeaseId, 64)
                + " stream=" + LogUtil.SanitizeForConsole(lease.PlaybackId, 64)
                + " segment=" + lease.SegmentIndex
                + " deadline_ms=" + lease.DeadlineMs
                + " input=" + LogUtil.RedactUrl(lease.InputUrl),
            "lease queued segment=" + lease.SegmentIndex
                + " deadline_ms=" + lease.DeadlineMs
                + " input=" + LogUtil.RedactUrl(lease.InputUrl));

        _ = Task.Run(async () =>
        {
            HelperLeaseRunResult result;
            try
            {
                result = await HelperLeaseWorker.RunAsync(
                    lease,
                    AppSettingsStore.Shared.Snapshot(),
                    AppContext.BaseDirectory,
                    _httpClient,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new HelperLeaseRunResult(
                    false,
                    "error",
                    ex.GetType().Name + ": " + ex.Message,
                    0,
                    0,
                    null,
                    null);
            }

            var frame = new HelperTranscodeResultFrame
            {
                LeaseId = lease.LeaseId,
                Success = result.Success,
                Status = result.Status,
                Error = result.Error,
                Bytes = result.Bytes,
                ElapsedMs = result.ElapsedMilliseconds,
                Encoder = result.Encoder,
                FfmpegVersion = result.FfmpegVersion,
            };

            string resultLine = "[mesh][helper] result sent lease=" + LogUtil.SanitizeForConsole(lease.LeaseId, 64)
                + " segment=" + lease.SegmentIndex
                + " success=" + result.Success
                + " status=" + LogUtil.SanitizeForConsole(result.Status, 64)
                + " bytes=" + result.Bytes
                + " elapsed_ms=" + result.ElapsedMilliseconds
                + " encoder=" + (result.Encoder ?? "<none>")
                + (string.IsNullOrWhiteSpace(result.Error)
                    ? ""
                    : " error=" + LogUtil.SanitizeForConsole(result.Error, 180));

            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, MeshJsonContext.Default.HelperTranscodeResultFrame);
                await SendTextFrameAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                if (result.Success)
                {
                    Logger.WriteDiagnostic(LogComponent.Helper, resultLine,
                        "result sent segment=" + lease.SegmentIndex
                            + " status=" + result.Status
                            + " bytes=" + result.Bytes
                            + " elapsed_ms=" + result.ElapsedMilliseconds);
                }
                else
                {
                    Logger.WarnDiagnostic(LogComponent.Helper, resultLine,
                        "result sent segment=" + lease.SegmentIndex
                            + " status=" + result.Status
                            + " bytes=" + result.Bytes
                            + " elapsed_ms=" + result.ElapsedMilliseconds
                            + (string.IsNullOrWhiteSpace(result.Error)
                                ? ""
                                : " error=" + LogUtil.SanitizeForConsole(result.Error, 120)));
                }
            }
            catch (Exception ex)
            {
                Logger.WarnDiagnostic(
                    LogComponent.Helper,
                    "[mesh][helper][warn] result send failed lease=" + LogUtil.SanitizeForConsole(lease.LeaseId, 64)
                        + " segment=" + lease.SegmentIndex
                        + " " + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160),
                    "result send failed segment=" + lease.SegmentIndex
                        + " " + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 120));
            }
        });
    }

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
        ConsoleUx.Warn(
            LogComponent.Mesh,
            "failing " + failedIds.Count + " pending requests reason=" + reason +
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
