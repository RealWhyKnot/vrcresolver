using System.Net;
using System.Runtime.Versioning;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Local-relay HTTP listener at http://127.0.0.1:{port}/. Trust gateway
// for AVPro: VRChat's AVPro player ships a built-in trusted-host
// allowlist that includes `*.youtube.com`. The hosts file maps
// `localhost.youtube.com -> 127.0.0.1`, so AVPro fetches
//   http://localhost.youtube.com:{port}/play/<session>/manifest.<ext>?target=<base64>
// AVPro's trust check passes on the localhost.youtube.com host; the listener
// decodes target=, forwards to the real upstream (typically
// `https://node1.whyknot.dev/api/proxy?...`), and streams bytes back.
//
// If the server-side playlist uses relative subresource URLs, AVPro resolves
// them under /play/<session>/ and the listener forwards them relative to the
// original target URL's directory. The relay does not parse or rewrite HLS.
// WhyKnot.dev owns manifest compatibility, tier routing, and transcoding.
//
// Absolute subresource URLs remain a server-side contract: the local relay
// cannot keep those on localhost without body rewriting, and body rewriting is
// intentionally out of this client path.
//
// Phase 1: HTTP-only. HTTPS + per-machine cert lifecycle is a separate
// follow-up. AVPro accepts plain http:// on a non-443 port for hostnames
// matching its allowlist (legacy implementation proved this for years).
//
// AOT-clean: HttpListener + HttpClient + System.IO. No reflection, no dynamic codegen.
[SupportedOSPlatform("windows")]
internal sealed partial class LocalRelayServer : IDisposable
{
    private static readonly TimeSpan UpstreamHeaderDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BodyIdleTimeout = TimeSpan.FromSeconds(30);
    private const int CopyBufferSize = 80 * 1024;

    private readonly int _port;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;
    private readonly LocalRelayTargetResolver _targets = new();
    private Task? _acceptLoop;

    // Verbose request/response trace gated on BuildInfo.IsDevBuild. Dev builds
    // (auto-version OR dirty source) emit per-request logs the user can paste
    // back when reporting an issue; release builds (clean -Version + clean
    // tree, what CI ships) stay quiet so the watchdog log isn't flooded
    // during normal use.
    private static readonly bool s_verbose = BuildInfo.IsDevBuild;
    private static long s_reqCounter;

    public LocalRelayServer(int port)
    {
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine("[relay][error] HttpListener.Start failed on port "
                + _port + ": " + ex.Message);
            throw;
        }
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* best-effort */ }
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening && !_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) break;
                Console.WriteLine("[relay][warn] accept error: " + ex.Message);
                continue;
            }
            // Fire-and-forget per-request handler. HttpListener serialises
            // accept; the dispatched task does the actual work in parallel.
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        reqCts.CancelAfter(UpstreamHeaderDeadline);
        string targetUrl = "";
        string reqId = s_verbose
            ? Interlocked.Increment(ref s_reqCounter).ToString("x6", System.Globalization.CultureInfo.InvariantCulture)
            : "";
        long t0 = s_verbose ? Environment.TickCount64 : 0;
        long bytesOut = 0;
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "";
            string method = ctx.Request.HttpMethod ?? "?";
            if (s_verbose)
            {
                string rawUrl = ctx.Request.RawUrl ?? "";
                string remote = ctx.Request.RemoteEndPoint?.ToString() ?? "?";
                string protoVer = "HTTP/" + (ctx.Request.ProtocolVersion?.ToString() ?? "?");
                Console.WriteLine("[relay] req=" + reqId + " <- " + method + " " + ShortUrl(rawUrl)
                    + " from=" + remote + " " + protoVer);
                DumpHeaders("[relay] req=" + reqId + "  H<", ctx.Request.Headers);
            }
            if (!LocalRelayTargetResolver.IsPlayPath(path))
            {
                if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " -> 404 (path mismatch)");
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            if (!_targets.TryResolve(
                    path,
                    ctx.Request.Url?.Query ?? "",
                    ctx.Request.QueryString["target"],
                    out LocalRelayTarget target))
            {
                if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " -> 400 (no target param or relative base)");
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            targetUrl = target.Url;
            if (string.IsNullOrEmpty(targetUrl) || !Uri.TryCreate(targetUrl, UriKind.Absolute, out _))
            {
                if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " -> 400 (invalid resolved target='" + ShortUrl(targetUrl) + "')");
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }
            if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " kind=" + target.Kind + " target=" + ShortUrl(targetUrl));

            using var outboundReq = new HttpRequestMessage(
                new HttpMethod(method), targetUrl);
            LocalRelayHeaders.ForwardRequestHeaders(ctx.Request, outboundReq);

            using var resp = await _http.SendAsync(
                outboundReq,
                HttpCompletionOption.ResponseHeadersRead,
                reqCts.Token).ConfigureAwait(false);

            if (s_verbose)
            {
                Console.WriteLine("[relay] req=" + reqId + " upstream-status=" + (int)resp.StatusCode
                    + " ms=" + (Environment.TickCount64 - t0));
                DumpHttpHeaders("[relay] req=" + reqId + "  upstream-H>", resp.Headers, resp.Content.Headers);
            }

            ctx.Response.StatusCode = (int)resp.StatusCode;
            string contentType = "";
            long? contentLength = null;
            int passedHeaders = 0;
            int droppedHeaders = 0;
            foreach (var h in resp.Headers.Concat(resp.Content.Headers))
            {
                if (LocalRelayHeaders.ShouldDropResponseHeader(h.Key)) { droppedHeaders++; continue; }
                if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(string.Join(", ", h.Value), out long len))
                        contentLength = len;
                    continue;
                }
                if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    contentType = string.Join(", ", h.Value);
                try { ctx.Response.Headers.Add(h.Key, string.Join(", ", h.Value)); passedHeaders++; }
                catch { /* HttpListener rejects some restricted headers; skip */ }
            }
            if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " response-headers passed=" + passedHeaders
                + " dropped=" + droppedHeaders + " ct='" + contentType + "' len=" + (contentLength?.ToString() ?? "null"));

            if (contentLength.HasValue) ctx.Response.ContentLength64 = contentLength.Value;
            if (s_verbose) DumpHeaders("[relay] req=" + reqId + "  H>", ctx.Response.Headers);

            // Binary stream copy with per-read idle CTS so a long video
            // (HLS segment, mp4 progressive) doesn't get killed by a
            // single deadline timer; idle = 30s without a byte = abort.
            using var upstream = await resp.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
            byte[] buf = new byte[CopyBufferSize];
            while (!_cts.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                idle.CancelAfter(BodyIdleTimeout);
                int n;
                try { n = await upstream.ReadAsync(buf.AsMemory(), idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested && idle.IsCancellationRequested)
                {
                    Console.WriteLine("[relay][warn] body idle timeout for "
                        + ShortUrl(targetUrl));
                    return;
                }
                if (n == 0) break;
                await ctx.Response.OutputStream.WriteAsync(buf.AsMemory(0, n), _cts.Token).ConfigureAwait(false);
                bytesOut += n;
            }
            if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " -> " + ctx.Response.StatusCode
                + " stream bytes-out=" + bytesOut + " elapsed=" + (Environment.TickCount64 - t0) + "ms");
        }
        catch (HttpListenerException hle)
        {
            if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " client-disconnect (HttpListenerException ec=" + hle.ErrorCode + ") bytes-out=" + bytesOut);
        }
        catch (System.IO.IOException ioe)
        {
            if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " client-disconnect (IOException: " + ioe.Message + ") bytes-out=" + bytesOut);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex)
        {
            Console.WriteLine("[relay][warn] handler error for " + ShortUrl(targetUrl)
                + ": " + ex.GetType().Name + ": " + ex.Message);
            try { ctx.Response.StatusCode = 502; } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* best-effort */ }
        }
    }

    private static void DumpHeaders(string prefix, System.Collections.Specialized.NameValueCollection headers)
    {
        if (headers == null) return;
        foreach (string? key in headers.AllKeys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            string val = headers[key] ?? "";
            if (val.Length > 200) val = val.Substring(0, 200) + "...";
            Console.WriteLine(prefix + " " + key + ": " + val);
        }
    }

    private static void DumpHttpHeaders(string prefix, System.Net.Http.Headers.HttpResponseHeaders respH, System.Net.Http.Headers.HttpContentHeaders contentH)
    {
        foreach (var h in respH.Concat(contentH))
        {
            string val = string.Join(", ", h.Value);
            if (val.Length > 200) val = val.Substring(0, 200) + "...";
            Console.WriteLine(prefix + " " + h.Key + ": " + val);
        }
    }

    private static string ShortUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "?";
        return url.Length > 100 ? url.Substring(0, 100) + "..." : url;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { /* best-effort */ }
        _http.Dispose();
        _cts.Dispose();
    }
}
