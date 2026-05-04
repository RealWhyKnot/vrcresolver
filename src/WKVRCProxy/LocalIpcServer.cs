using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Named-pipe server at \\.\pipe\WKVRCProxy.resolve. The patched yt-dlp.exe
// connects, sends one ResolveRequest, reads one ResolveResponse, and closes.
// Default ACL = current user only — patched yt-dlp runs as the same user.
//
// Wire format on the pipe is newline-delimited JSON: client writes one
// request followed by '\n', server writes one response followed by '\n'.
// Newline framing keeps both sides simple — no length prefixes, no
// read-to-end hangs that would happen with raw stream deserialization.
//
// Per-connection budget is 10s. On timeout/parse-error/MeshClient throwing
// we synthesize a fallback_native frame with the appropriate reason rather
// than dropping the connection, so the patched yt-dlp.exe always gets a
// definitive answer it can act on.
[SupportedOSPlatform("windows")]
internal sealed class LocalIpcServer : IDisposable
{
    // Per-request budget. Sized so the server has room to escalate from
    // its standard tier (yt-dlp:youtube-tv-combo, ~3-8 s) to a heavier
    // tier (browser-extract, vrchat-impersonate) without the watchdog
    // synthesizing a fallback_native too eagerly. The wrapper's read
    // budget (18 s) sits above this so the synthesized response always
    // wins the race when this timeout fires.
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(15);
    // Match the WS-side 4 MiB cap so a giant vrchat_format_arg (raw yt-dlp
    // -f selector) round-trips end-to-end. Pre-fix this was 64 KiB which
    // silently truncated large selectors mid-string; the resulting
    // truncated JSON failed to parse and surfaced as fallback_internal_error
    // with no diagnostic about WHY.
    private const int MaxRequestBytes = 4 * 1024 * 1024;

    private readonly MeshClient _mesh;
    private readonly CancellationTokenSource _cts = new();
    private Task? _accepter;

    public LocalIpcServer(MeshClient mesh)
    {
        _mesh = mesh;
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
                pipe = new NamedPipeServerStream(
                    WireConstants.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ipc] could not create pipe instance: " + ex.Message);
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
                Console.WriteLine("[ipc] accept error: " + ex.Message);
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
        try
        {
            var (line, truncated) = await ReadLineAsync(pipe, perReqCts.Token).ConfigureAwait(false);
            if (truncated)
            {
                Console.WriteLine("[ipc] rejecting request: payload exceeded "
                    + MaxRequestBytes + " bytes without a newline terminator");
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            ResolveRequest? req = null;
            string? parseError = null;
            if (!string.IsNullOrWhiteSpace(line))
            {
                try { req = JsonSerializer.Deserialize<ResolveRequest>(line); }
                catch (Exception ex) { parseError = ex.GetType().Name + ": " + ex.Message; }
            }

            if (req == null || string.IsNullOrEmpty(req.Url))
            {
                // Surface parse failures + missing-url cases so a misbehaving
                // patched yt-dlp is diagnosable from the watchdog console.
                // Pre-fix this path was completely silent.
                if (parseError != null)
                {
                    Console.WriteLine("[ipc] request parse failed: "
                        + LogUtil.SanitizeForConsole(parseError, 160)
                        + " preview=" + LogUtil.SanitizeForConsole(line, 80));
                }
                else if (req != null)
                {
                    Console.WriteLine("[ipc] request missing url");
                }
                else
                {
                    Console.WriteLine("[ipc] empty request received");
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
                Console.WriteLine("[ipc] rejecting request id=" + id +
                    " action=" + LogUtil.SanitizeForConsole(req.Action, 32) +
                    " — only \"resolve\" is accepted on this pipe");
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
                Console.WriteLine("[ipc] rejecting request id=" + id + CidSuffix(cid) +
                    " player=" + LogUtil.SanitizeForConsole(req.Player ?? "<null>", 32) +
                    " — must be \"avpro\" or \"unity\" (case-sensitive)");
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            JsonDocument? respDoc = null;
            string? failReason = null;
            string outcome = "?";
            try
            {
                // Lossless forward: hand the whole DTO to MeshClient so v2 fields
                // (protocol_version / accept_protocols / accept_codecs / etc.)
                // and any unknown fields populated by the patched yt-dlp pass
                // through to the mesh server unchanged. The DTO's
                // [JsonExtensionData] bag preserves anything we don't statically
                // know about.
                respDoc = await _mesh.ResolveAsync(req, perReqCts.Token).ConfigureAwait(false);
                await WriteDocAsync(pipe, respDoc, perReqCts.Token).ConfigureAwait(false);

                // Determine outcome from the response action for the success log.
                if (respDoc.RootElement.TryGetProperty("action", out var actEl)
                    && actEl.ValueKind == JsonValueKind.String)
                {
                    outcome = actEl.GetString() ?? "?";
                }
            }
            catch (OperationCanceledException)
            {
                failReason = WireConstants.FallbackServerUnreachable;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[ipc] mesh.ResolveAsync threw id=" + id + CidSuffix(cid) +
                    ": " + ex.GetType().Name + ": " +
                    LogUtil.SanitizeForConsole(ex.Message, 160));
                failReason = WireConstants.FallbackInternalError;
            }
            finally
            {
                respDoc?.Dispose();
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

            // Per-request success log. Captures id, optional correlation_id,
            // player, and the outcome action so users have a baseline of
            // resolves-served they can correlate against failures.
            Console.WriteLine(
                "[ipc] resolve id=" + id + CidSuffix(cid) +
                " player=" + LogUtil.SanitizeForConsole(req.Player ?? WireConstants.PlayerUnknown, 16) +
                " outcome=" + LogUtil.SanitizeForConsole(outcome, 48));
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                "[ipc] connection error id=" + id + CidSuffix(cid) +
                ": " + ex.GetType().Name + ": " +
                LogUtil.SanitizeForConsole(ex.Message, 160));
        }
        finally
        {
            try { if (pipe.IsConnected) pipe.Disconnect(); } catch { /* ignore */ }
            pipe.Dispose();
        }
    }

    // " cid=<id>" suffix only when correlation_id is populated.
    private static string CidSuffix(string? correlationId) =>
        string.IsNullOrEmpty(correlationId) ? "" : " cid=" + LogUtil.SanitizeForConsole(correlationId, 64);

    // Pre-baked single-byte newline terminator. Used twice per response
    // (success + fallback paths). Avoids allocating a fresh byte[] per write.
    private static readonly byte[] NewlineFrame = new byte[] { (byte)'\n' };

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

    private static async Task WriteDocAsync(Stream s, JsonDocument doc, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(doc.RootElement);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.WriteAsync(NewlineFrame, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    // Skip null fields when serializing the synthetic fallback frame so the
    // wire shape stays v1-identical for v1 patched-yt-dlp consumers. Without
    // this, the v2 ResolveResponse fields (container, video_codec, etc.)
    // would each emit "field":null, forcing every fallback recipient to
    // tolerate keys it doesn't know.
    private static readonly JsonSerializerOptions FallbackSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task WriteFallbackAsync(Stream s, string id, string reason, CancellationToken ct)
    {
        var frame = new ResolveResponse
        {
            Action = WireConstants.ActionFallbackNative,
            Id = id,
            Reason = reason,
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(frame, FallbackSerializerOptions);
        try
        {
            await s.WriteAsync(payload, ct).ConfigureAwait(false);
            await s.WriteAsync(NewlineFrame, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }
        catch { /* peer may have hung up — we tried */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
