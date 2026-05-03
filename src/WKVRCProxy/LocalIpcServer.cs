using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
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
        try
        {
            string? line = await ReadLineAsync(pipe, perReqCts.Token).ConfigureAwait(false);
            ResolveRequest? req = null;
            if (!string.IsNullOrWhiteSpace(line))
            {
                try { req = JsonSerializer.Deserialize<ResolveRequest>(line); }
                catch { /* leave req null */ }
            }

            if (req == null || string.IsNullOrEmpty(req.Url))
            {
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            id = req.Id ?? "";

            JsonDocument? respDoc = null;
            string? failReason = null;
            try
            {
                respDoc = await _mesh.ResolveAsync(req.Url, req.Player, req.MaxHeight, perReqCts.Token).ConfigureAwait(false);
                await WriteDocAsync(pipe, respDoc, perReqCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                failReason = WireConstants.FallbackServerUnreachable;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ipc] mesh.ResolveAsync threw: " + ex.Message);
                failReason = WireConstants.FallbackInternalError;
            }
            finally
            {
                respDoc?.Dispose();
            }

            if (failReason != null)
                await WriteFallbackAsync(pipe, id, failReason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ipc] connection error: " + ex.Message);
        }
        finally
        {
            try { if (pipe.IsConnected) pipe.Disconnect(); } catch { /* ignore */ }
            pipe.Dispose();
        }
    }

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

    private static async Task WriteFallbackAsync(Stream s, string id, string reason, CancellationToken ct)
    {
        var frame = new ResolveResponse
        {
            Action = WireConstants.ActionFallbackNative,
            Id = id,
            Reason = reason,
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(frame);
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
