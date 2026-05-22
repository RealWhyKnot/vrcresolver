using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Named-pipe server at \\.\pipe\WKVRCProxy.resolve. The patched yt-dlp.exe
// connects, sends one ResolveRequest, reads one ResolveResponse, and closes.
//
// ACL: pipe is created with an explicit security descriptor that grants the
// current user full access (DACL) AND tags the pipe with a Low-integrity
// mandatory label (SACL `S:(ML;;NW;;;LW)`). Without the Low-integrity SACL,
// the wrapper deployed into VRChat's Tools dir (Low-integrity, inherited
// from the LocalLow path) can't connect — Windows MIC blocks the connect
// attempt before the DACL check fires. This was a silent bug for an entire
// session: VRChat invoked our wrapper, wrapper's pipe connect failed, wrapper
// silently fell through to og fallback. Mesh path bypassed entirely.
//
// Wire format on the pipe is newline-delimited JSON: client writes one
// request followed by '\n', server writes one response followed by '\n'.
// Newline framing keeps both sides simple — no length prefixes, no
// read-to-end hangs that would happen with raw stream deserialization.
//
// Per-connection budget is 15 s. On timeout/parse-error/MeshClient throwing
// we synthesize a fallback_native frame with the appropriate reason rather
// than dropping the connection, so the patched yt-dlp.exe always gets a
// definitive answer it can act on.
[SupportedOSPlatform("windows")]
internal sealed partial class LocalIpcServer : IDisposable
{
    // Default per-request budget when the wrapper does not declare its
    // own deadline (old clients, manual JSON over the pipe, etc.). The
    // wrapper now sends `wrapper_deadline_ms` on every resolve and the
    // watchdog overrides this default so the mesh-side wait aligns with
    // however long the wrapper is actually willing to wait, minus a
    // 500 ms safety margin so the synthesized fallback_native still
    // wins the race if the timeout fires.
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(15);
    // Hard floor + ceiling on the per-request budget when honoring a
    // wrapper-declared deadline. The floor prevents a zero / tiny budget
    // from making every resolve insta-fail. The ceiling caps trust in a
    // misbehaving wrapper to a value still well below the WS keepalive.
    private const int WrapperBudgetFloorMs = 5_000;
    private const int WrapperBudgetCeilingMs = 90_000;
    private const int WrapperBudgetSafetyMarginMs = 500;
    // Match the WS-side 4 MiB cap so a giant vrchat_format_arg (raw yt-dlp
    // -f selector) round-trips end-to-end. Pre-fix this was 64 KiB which
    // silently truncated large selectors mid-string; the resulting
    // truncated JSON failed to parse and surfaced as fallback_internal_error
    // with no diagnostic about WHY.
    private const int MaxRequestBytes = 4 * 1024 * 1024;

    private readonly MeshClient _mesh;
    private readonly ResolveCache? _cache;
    private readonly OgFallbackHint? _ogFallbackHint;
    private readonly CancellationTokenSource _cts = new();
    private Task? _accepter;

    public LocalIpcServer(MeshClient mesh, ResolveCache? cache = null, OgFallbackHint? ogFallbackHint = null)
    {
        _mesh = mesh;
        _cache = cache;
        _ogFallbackHint = ogFallbackHint;
    }

    public void Start()
    {
        _accepter = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_accepter != null)
        {
            try { await _accepter.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipeWithLowIntegrityLabel();
            }
            catch (Exception ex)
            {
                ConsoleUx.Warn(LogComponent.Ipc, "could not create pipe instance: " + ex.Message);
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return;
            }
            catch (Exception ex)
            {
                ConsoleUx.Warn(LogComponent.Ipc, "accept failed: " + ex.Message);
                pipe.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleAsync(pipe, ct));
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken outerCt)
    {
        using var perReqCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        perReqCts.CancelAfter(PerRequestTimeout);
        string id = "";
        string? cid = null;
        var swReq = Stopwatch.StartNew();
        try
        {
            var (line, truncated) = await ReadLineAsync(pipe, perReqCts.Token).ConfigureAwait(false);
            if (truncated)
            {
                ConsoleUx.Warn(LogComponent.Ipc, "rejecting request: payload exceeded "
                    + MaxRequestBytes + " bytes without a newline terminator");
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            // v3.2: peek the action field FIRST. If it's one of the
            // wrapper's notification frames (og_fallback_notify or
            // wrapper_og_failed), dispatch separately -- the wire shape
            // is a different DTO (WrapperEventNotify) and the wrapper
            // closes the pipe immediately after writing without waiting
            // for a response.
            if (!string.IsNullOrWhiteSpace(line) && LooksLikeWrapperEventNotify(line))
            {
                try
                {
                    var notify = JsonSerializer.Deserialize(line, MeshJsonContext.Default.WrapperEventNotify);
                    if (notify != null) HandleWrapperEvent(notify);
                }
                catch (Exception ex)
                {
                    Logger.WriteFileOnly("[wrapper][warn] wrapper event parse failed: "
                        + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
                }
                return;
            }

            ResolveRequest? req = null;
            string? parseError = null;
            if (!string.IsNullOrWhiteSpace(line))
            {
                try { req = JsonSerializer.Deserialize(line, MeshJsonContext.Default.ResolveRequest); }
                catch (Exception ex) { parseError = ex.GetType().Name + ": " + ex.Message; }
            }

            if (req == null || string.IsNullOrEmpty(req.Url))
            {
                // Surface parse failures + missing-url cases so a misbehaving
                // patched yt-dlp is diagnosable from the watchdog console.
                // Pre-fix this path was completely silent.
                if (parseError != null)
                {
                    ConsoleUx.Warn(LogComponent.Ipc, "request parse failed: "
                        + LogUtil.SanitizeForConsole(parseError, 160)
                        + " preview=" + LogUtil.SanitizeForConsole(line, 80));
                }
                else if (req != null)
                {
                    ConsoleUx.Warn(LogComponent.Ipc, "request missing url");
                }
                else
                {
                    ConsoleUx.Warn(LogComponent.Ipc, "empty request received");
                }
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            id = req.Id ?? "";
            cid = req.CorrelationId;

            // H12: validate action vocabulary. The DTO accepts any string;
            // a request with action="ping" or any non-resolve verb that
            // happens to also carry a url would otherwise be silently
            // forwarded to the mesh (which would reject — but with no
            // diagnostic on the watchdog side).
            if (!string.Equals(req.Action, WireConstants.ActionResolve, StringComparison.Ordinal))
            {
                ConsoleUx.Warn(LogComponent.Ipc, "rejecting request id=" + id +
                    " action=" + LogUtil.SanitizeForConsole(req.Action, 32) +
                    " -- only \"resolve\" is accepted on this pipe");
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            // H11: validate player vocabulary. Server spec is case-sensitive
            // "avpro" | "unity"; anything else (including null/empty,
            // "AVPro", "AvPro") gets rejected here with a clear log line so
            // patched-yt-dlp casing drift surfaces in a bug report instead
            // of silently being routed to a server that will reject.
            if (req.Player != WireConstants.PlayerAvPro && req.Player != WireConstants.PlayerUnity)
            {
                ConsoleUx.Warn(LogComponent.Ipc, "rejecting request id=" + id + CidSuffix(cid) +
                    " player=" + LogUtil.SanitizeForConsole(req.Player ?? "<null>", 32) +
                    " -- must be \"avpro\" or \"unity\" (case-sensitive)");
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            // If the wrapper declared its own deadline, align the watchdog's
            // per-request budget with it (minus a 500 ms safety margin so the
            // synthesized fallback_native lands before the wrapper gives up).
            // Old wrappers that omit the field keep the PerRequestTimeout
            // default armed at the top of HandleAsync. Floor and ceiling
            // bound the trust placed in a misbehaving wrapper.
            if (req.WrapperDeadlineMs is int wrapperBudgetMs && wrapperBudgetMs > 0)
            {
                int effectiveMs = Math.Clamp(
                    wrapperBudgetMs - WrapperBudgetSafetyMarginMs,
                    WrapperBudgetFloorMs,
                    WrapperBudgetCeilingMs);
                perReqCts.CancelAfter(TimeSpan.FromMilliseconds(effectiveMs));
                Logger.WriteFileOnly("[ipc] honoring wrapper_deadline_ms id=" + id +
                    " wrapper_deadline_ms=" + wrapperBudgetMs +
                    " effective_ms=" + effectiveMs);
            }

            // Capture the host + player labels for the single per-resolve
            // summary line that fires at terminal-response time below.
            // The earlier two-line layout (cyan request line at arrival
            // + colored response line at terminus) was traded for one
            // line per resolve so busy worlds don't double-scroll.
            //
            // `[via lh-yt]` fires when the user-pasted URL host is
            // localhost.youtube.com -- the public-instance trust-list
            // bypass path. Surfaces at-a-glance whether the
            // public-world workaround is being exercised. Same
            // per-process counter goes to the heartbeat line for
            // aggregate visibility.
            string host = ExtractHost(req.Url);
            bool viaLhYt = IsLocalhostYoutubeUrl(req.Url);
            string playerLabel = FormatPlayerLabel(req);
            WatchdogStats.RecordResolve(viaLhYt);

            string? failReason = null;
            string outcome = "?";
            string? serverReason = null;
            bool viaCache = false;
            string nodeHost = _mesh.CurrentNodeHost;

            // Reactive og-fallback: an AVPro load_failure for this source
            // within the last ~60 s short-circuits the entire mesh path so
            // the wrapper execs yt-dlp-og.exe immediately. Set by
            // VrcLogMonitor on the failing playback's resolved URL and
            // unwound by TTL. Cache-hit path is intentionally bypassed too:
            // the cache entry is likely the one that produced the failure,
            // and even if VrcLogMonitor already evicted it the goal is to
            // give VRChat a known-good native URL on the next retry.
            if (_ogFallbackHint != null && _ogFallbackHint.ShouldPreferOg(req.Url))
            {
                await WriteFallbackAsync(pipe, id,
                    WireConstants.OgFallbackReasonPriorLoadFailure,
                    perReqCts.Token).ConfigureAwait(false);
                ConsoleUx.Write(LogComponent.Ipc,
                    "og-fallback (prior load_failure) id=" + id
                        + CidSuffix(cid)
                        + " host=" + ExtractHost(req.Url));
                return;
            }

            try
            {
                // v3.2: resolve disk-cache lookup. If we have a cached
                // `resolved` frame for (nodeHost, url, player, format)
                // whose server-issued expires_at is still > now + 30s,
                // replay it directly to the wrapper -- skip the WS
                // round-trip + server-side lookup. Cache cap = 500
                // entries; staleness is closed via VrcLogMonitor's
                // silent_stall hook calling EvictByUrl.
                CachedResolve? cached = _cache?.Lookup(nodeHost, req.Url, req.Player, req.VrchatFormatArg, req.Id ?? "");
                if (cached.HasValue)
                {
                    await WriteFrameAsync(pipe, cached.Value.Frame, perReqCts.Token).ConfigureAwait(false);
                    outcome = cached.Value.Action;
                    serverReason = cached.Value.Reason;
                    viaCache = true;
                    WatchdogStats.RecordCacheHit();
                    Logger.WriteFileOnly("[resolve-cache] hit id=" + id +
                        " host=" + ExtractHost(req.Url) +
                        " bytes=" + cached.Value.Frame.Length);
                }
                else
                {
                    // Lossless forward: hand the whole DTO to MeshClient so v2 fields
                    // (protocol_version / accept_protocols / accept_codecs / etc.)
                    // and any unknown fields populated by the patched yt-dlp pass
                    // through to the mesh server unchanged. The DTO's
                    // [JsonExtensionData] bag preserves anything we don't statically
                    // know about.
                    //
                    // ResolveAsync returns the verified raw response bytes plus
                    // the pre-extracted action and server-supplied reason. We
                    // write the bytes straight to the pipe -- no JsonDocument
                    // re-encode on the hot path -- and use the extracted strings
                    // for the user-facing console summary.
                    MeshResolveResult result = await _mesh.ResolveAsync(req, perReqCts.Token).ConfigureAwait(false);

                    // Brief retry on transient reasons. The server may
                    // have been mid-discovery (no strategies built yet
                    // for this domain) or briefly unreachable; a second
                    // attempt 2s later often succeeds with a real
                    // strategy. Structural reasons (domain_blocked,
                    // all_configs_failed, extractor_unsupported,
                    // unity_unsupported_format) won't change on retry,
                    // so we skip them and let the wrapper fall back to
                    // og immediately.
                    if (result.Action == WireConstants.ActionFallbackNative
                        && IsRetryableFallback(result.Reason)
                        && !perReqCts.Token.IsCancellationRequested)
                    {
                        Logger.WriteFileOnly("[ipc] retry id=" + id + CidSuffix(cid)
                            + " reason=" + LogUtil.SanitizeForConsole(result.Reason ?? "?", 32)
                            + " attempt=2");
                        bool slept;
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2), perReqCts.Token).ConfigureAwait(false);
                            slept = true;
                        }
                        catch (OperationCanceledException) { slept = false; }

                        if (slept && !perReqCts.Token.IsCancellationRequested)
                        {
                            // Fresh request id so the server doesn't
                            // dedupe against an in-flight entry for the
                            // same id. The DTO carries everything else
                            // verbatim from the first attempt.
                            req.Id = Guid.NewGuid().ToString("N");
                            result = await _mesh.ResolveAsync(req, perReqCts.Token).ConfigureAwait(false);
                        }
                    }

                    await WriteFrameAsync(pipe, result.Frame, perReqCts.Token).ConfigureAwait(false);
                    outcome = result.Action;
                    serverReason = result.Reason;

                    // Cache the response on terminal `resolved` with a
                    // non-null expires_at. ResolveCache.Store gates these
                    // conditions itself; we still parse the frame here so
                    // the typed ResolveResponse round-trips cleanly through
                    // the source-gen path on subsequent hits.
                    if (_cache != null && outcome == WireConstants.ActionResolved && !string.IsNullOrEmpty(nodeHost))
                    {
                        try
                        {
                            var parsed = JsonSerializer.Deserialize(result.Frame, MeshJsonContext.Default.ResolveResponse);
                            if (parsed != null)
                            {
                                bool defaultTtl = string.IsNullOrEmpty(parsed.ExpiresAt);
                                _cache.Store(nodeHost, req.Url, req.Player, req.VrchatFormatArg, parsed);
                                Logger.WriteFileOnly("[resolve-cache] stored id=" + id +
                                    " host=" + ExtractHost(req.Url) +
                                    (defaultTtl ? " ttl=default" : " ttl=server"));
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteFileOnly("[resolve-cache] store failed id=" + id +
                                ": " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                failReason = WireConstants.FallbackServerUnreachable;
            }
            catch (Exception ex)
            {
                ConsoleUx.Warn(
                    LogComponent.Ipc,
                    "mesh.ResolveAsync threw id=" + id + CidSuffix(cid) +
                    ": " + ex.GetType().Name + ": " +
                    LogUtil.SanitizeForConsole(ex.Message, 160));
                failReason = WireConstants.FallbackInternalError;
            }

            if (failReason != null)
            {
                outcome = WireConstants.ActionFallbackNative + "/" + failReason;
                await WriteFallbackAsync(pipe, id, failReason, CancellationToken.None).ConfigureAwait(false);
                ReportingService.ReportFallback(req, failReason, null);
            }
            else if (outcome.StartsWith(WireConstants.ActionFallbackNative))
            {
                // Mesh returned a fallback_native frame. Reach into the
                // dispatched response for the reason code; ReportingService
                // filters out transient kinds itself.
                string reason = outcome.Length > WireConstants.ActionFallbackNative.Length + 1
                    ? outcome[(WireConstants.ActionFallbackNative.Length + 1)..]
                    : "";
                if (!string.IsNullOrEmpty(reason))
                    ReportingService.ReportFallback(req, reason, null);
            }

            // User-facing per-resolve summary -- single line per resolve.
            // Format:
            //   <host> [via lh-yt] (<player>)  <status>  <elapsed>
            // Colour signals at-a-glance status: green = resolved (mesh
            // or cached), yellow = server replied with fallback_native
            // (og takes over), red = we synthesised fallback_native
            // locally (server timeout / IPC budget tripped), gray =
            // unexpected outcome. Standing rule from
            // feedback_no_console_spam.md: one summary line per resolve,
            // not a START + END pair.
            swReq.Stop();
            ResolveStatus status;
            string? reasonForLine = null;
            if (outcome == WireConstants.ActionResolved)
            {
                status = ResolveStatus.Resolved;
            }
            else if (failReason != null)
            {
                status = ResolveStatus.Failed;
                reasonForLine = failReason;
            }
            else if (outcome == WireConstants.ActionFallbackNative)
            {
                status = ResolveStatus.Fallback;
                reasonForLine = !string.IsNullOrEmpty(serverReason) ? serverReason : "unspecified";
            }
            else
            {
                status = ResolveStatus.Unexpected;
                reasonForLine = outcome;
            }
            ConsoleUx.ResolveOutcome(
                host: host,
                player: playerLabel,
                status: status,
                viaCache: viaCache,
                viaLhYt: viaLhYt,
                elapsed: swReq.Elapsed,
                reason: reasonForLine);

            // Detailed per-request line (id, cid, full outcome) routed to
            // the rolling watchdog log only -- kept off the user-facing
            // console window so the friendly summary above stays scannable.
            Logger.WriteFileOnly(
                "[ipc] resolve_dispatch_complete id=" + id + CidSuffix(cid) +
                " action=" + LogUtil.SanitizeForConsole(outcome, 48) +
                " reason=" + LogUtil.SanitizeForConsole(serverReason ?? failReason ?? "", 48) +
                " player=" + LogUtil.SanitizeForConsole(req.Player ?? WireConstants.PlayerUnknown, 16) +
                (viaCache ? " via=cache" : "") +
                " elapsed_ms=" + (long)swReq.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            ConsoleUx.Warn(
                LogComponent.Ipc,
                "connection error id=" + id + CidSuffix(cid) +
                ": " + ex.GetType().Name + ": " +
                LogUtil.SanitizeForConsole(ex.Message, 160));
        }
        finally
        {
            try { if (pipe.IsConnected) pipe.Disconnect(); } catch { /* ignore */ }
            pipe.Dispose();
        }
    }

    private static bool IsRetryableFallback(string? reason) =>
        reason == WireConstants.FallbackDiscoveryInProgress
        || reason == WireConstants.FallbackServerUnreachable;

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

    private void HandleWrapperEvent(WrapperEventNotify notify)
    {
        if (string.Equals(notify.Action, WireConstants.ActionWrapperOgFailedNotify, StringComparison.Ordinal))
        {
            HandleOgFailedNotify(notify);
            return;
        }
        HandleOgFallbackNotify(notify);
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

    // " cid=<id>" suffix only when correlation_id is populated.
    private static string CidSuffix(string? correlationId) =>
        string.IsNullOrEmpty(correlationId) ? "" : " cid=" + LogUtil.SanitizeForConsole(correlationId, 64);

    // Append the NDJSON framing newline to a payload byte[] so the wire
    // send is one WriteAsync instead of two (payload + separate newline).
    // Named pipes (PIPE_TYPE_BYTE | PIPE_WAIT) dispatch the write atomically,
    // so coalescing also lets the caller drop the explicit FlushAsync that
    // used to follow the newline write.
    private static byte[] AppendNewline(byte[] payload)
    {
        byte[] framed = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, framed, 0, payload.Length);
        framed[payload.Length] = (byte)'\n';
        return framed;
    }

    // Returns the line, or null on empty connection. Sets `truncated` to
    // true if MaxRequestBytes was hit before a '\n' arrived — the caller
    // can then surface a "request_too_large" diagnostic instead of
    // confusing "malformed JSON" (which is what JsonSerializer would
    // report against a truncated payload).
    //
    // Buffered: one ReadAsync per 4 KiB chunk, then scan in-process for the
    // newline terminator. Pre-fix this read one byte per syscall — a 100 KiB
    // request needed 100k async syscalls.
    private static async Task<(string? Line, bool Truncated)> ReadLineAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        bool sawNewline = false;
        while (ms.Length < MaxRequestBytes)
        {
            int n = await s.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) break;
            int consume = n;
            int nlIdx = Array.IndexOf(buf, (byte)'\n', 0, n);
            if (nlIdx >= 0) { sawNewline = true; consume = nlIdx; }
            for (int i = 0; i < consume && ms.Length < MaxRequestBytes; i++)
            {
                byte b = buf[i];
                if (b == (byte)'\r') continue;
                ms.WriteByte(b);
            }
            if (sawNewline) break;
        }
        if (ms.Length == 0) return (null, false);
        bool truncated = !sawNewline && ms.Length >= MaxRequestBytes;
        return (Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length), truncated);
    }

    // Pass through pre-serialized JSON bytes from MeshClient. Appends the
    // NDJSON framing newline in-place and writes once. No JsonDocument
    // re-encode on the hot path — earlier impl took a JsonDocument and
    // called SerializeToUtf8Bytes(doc.RootElement) here, which re-emitted
    // the same JSON the dispatch handler had just parsed.
    private static async Task WriteFrameAsync(Stream s, byte[] frame, CancellationToken ct)
    {
        byte[] payload = AppendNewline(frame);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    // Skip null fields when serializing the synthetic fallback frame so the
    // wire shape stays v1-identical for v1 patched-yt-dlp consumers. Without
    // this, the v2 ResolveResponse fields (container, video_codec, etc.)
    // would each emit "field":null, forcing every fallback recipient to
    // tolerate keys it doesn't know.
    //
    // AOT migration: the WhenWritingNull options used to live here as
    // FallbackSerializerOptions, now baked into MeshFallbackJsonContext
    // via [JsonSourceGenerationOptions(DefaultIgnoreCondition = ...)].
    // The source-gen produces a parallel formatter set for ResolveResponse
    // with the omit-nulls behaviour applied at codegen time.

    private static async Task WriteFallbackAsync(Stream s, string id, string reason, CancellationToken ct)
    {
        var frame = new ResolveResponse
        {
            Action = WireConstants.ActionFallbackNative,
            Id = id,
            Reason = reason,
        };
        byte[] payload = AppendNewline(
            JsonSerializer.SerializeToUtf8Bytes(frame, MeshFallbackJsonContext.Default.ResolveResponse));
        try
        {
            await s.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        catch { /* peer may have hung up -- we tried */ }
    }

    // True iff the URL's host is exactly `localhost.youtube.com`. Used for
    // the `[via lh-yt]` console tag and the heartbeat's via-lh-yt counter.
    // Match is exact (not substring); a longer host like
    // `notlocalhost.youtube.com` does NOT count.
    private static bool IsLocalhostYoutubeUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                return u.Host.Equals(HostsManager.MarkerHost, StringComparison.OrdinalIgnoreCase);
        }
        catch { /* best-effort */ }
        return false;
    }

    // Bare hostname (host minus optional "www." prefix) for the user-facing
    // per-resolve summary. Path / query are NEVER printed to console — they
    // can carry user-identifying tokens (YouTube video ids, twitch streams,
    // etc.). The full URL stays in the watchdog log file via Logger.
    private static string ExtractHost(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                string h = u.Host;
                if (h.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) h = h[4..];
                return h;
            }
        }
        catch { /* best-effort */ }
        return "?";
    }

    [GeneratedRegex(@"height<=(\d+)")]
    private static partial Regex HeightCapRegex();

    // Player + target resolution label for the request line. The wrapper
    // doesn't populate maxHeight today (the constraint lives in the
    // vrchat_format_arg's `[height<=N]` selector instead) so we parse that
    // when the explicit field is absent. Falls back to "max" when neither
    // is available.
    private static string FormatPlayerLabel(ResolveRequest req)
    {
        string player = req.Player == WireConstants.PlayerUnity ? "Unity" : "AVPro";
        if (req.MaxHeight is int mh && mh > 0)
            return player + " " + mh + "p";
        if (!string.IsNullOrEmpty(req.VrchatFormatArg))
        {
            var m = HeightCapRegex().Match(req.VrchatFormatArg);
            if (m.Success) return player + " " + m.Groups[1].Value + "p";
        }
        return player + " max";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
