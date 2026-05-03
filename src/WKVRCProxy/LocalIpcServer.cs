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
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(10);
    private const int MaxRequestBytes = 64 * 1024;

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
            string? line = await ReadLineAsync(pipe, perReqCts.Token).ConfigureAwait(false);
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

    private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        byte[] one = new byte[1];
        while (ms.Length < MaxRequestBytes)
        {
            int n = await s.ReadAsync(one, 0, 1, ct).ConfigureAwait(false);
            if (n == 0) break;
            if (one[0] == (byte)'\n') break;
            if (one[0] == (byte)'\r') continue;
            ms.WriteByte(one[0]);
        }
        if (ms.Length == 0) return null;
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static async Task WriteDocAsync(Stream s, JsonDocument doc, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(doc.RootElement);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.WriteAsync(new byte[] { (byte)'\n' }, ct).ConfigureAwait(false);
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
            await s.WriteAsync(new byte[] { (byte)'\n' }, ct).ConfigureAwait(false);
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
