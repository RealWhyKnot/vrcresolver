using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Local-relay HTTP listener at http://127.0.0.1:{port}/. Trust gateway
// for AVPro: VRChat's AVPro player ships a built-in trusted-host
// allowlist that includes `*.youtube.com`. The hosts file maps
// `localhost.youtube.com -> 127.0.0.1`, so AVPro fetches
//   http://localhost.youtube.com:{port}/play?target=<base64-of-real-url>
// passes the trust check, and lands on this listener which forwards the
// request to the real resolved URL (typically
// `https://node1.whyknot.dev/api/proxy?q=...`) and streams bytes back.
//
// HLS handling: when the upstream Content-Type is application/vnd.apple.mpegurl
// or the URL contains `.m3u8`, the listener reads the manifest as text and
// rewrites every segment URL through the same `/play?target=...` shape so
// AVPro's segment fetches also pass the trust check. Without the rewrite the
// server-rewritten manifest points at `node1.whyknot.dev/api/proxy?url=...`
// for each segment which AVPro rejects the same way it rejected the manifest.
//
// Phase 1: HTTP-only. HTTPS + per-machine cert lifecycle is a separate
// follow-up. AVPro accepts plain http:// on a non-443 port for hostnames
// matching its allowlist (legacy implementation proved this for years).
//
// AOT-clean: HttpListener + HttpClient + System.IO + GeneratedRegex.
// No reflection, no dynamic codegen.
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
    private Task? _acceptLoop;

    public LocalRelayServer(int port)
    {
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        // SocketsHttpHandler with sane defaults: connect pool keep-alive,
        // automatic decompression off (AVPro consumes the upstream content
        // verbatim; we don't decompress).
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
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
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "";
            if (!path.StartsWith("/play", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            string? targetParam = ctx.Request.QueryString["target"];
            if (string.IsNullOrEmpty(targetParam))
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }
            targetUrl = DecodeTargetParam(targetParam);
            if (string.IsNullOrEmpty(targetUrl) || !Uri.TryCreate(targetUrl, UriKind.Absolute, out _))
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            using var outboundReq = new HttpRequestMessage(
                new HttpMethod(ctx.Request.HttpMethod), targetUrl);
            ForwardRequestHeaders(ctx.Request, outboundReq);

            using var resp = await _http.SendAsync(
                outboundReq,
                HttpCompletionOption.ResponseHeadersRead,
                reqCts.Token).ConfigureAwait(false);

            ctx.Response.StatusCode = (int)resp.StatusCode;
            string contentType = "";
            long? contentLength = null;
            foreach (var h in resp.Headers.Concat(resp.Content.Headers))
            {
                if (string.Equals(h.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(string.Join(", ", h.Value), out long len))
                        contentLength = len;
                    continue;
                }
                if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    contentType = string.Join(", ", h.Value);
                try { ctx.Response.Headers.Add(h.Key, string.Join(", ", h.Value)); }
                catch { /* HttpListener rejects some restricted headers; skip */ }
            }

            bool isHls = LooksLikeHls(contentType, targetUrl);
            if (isHls && resp.StatusCode == HttpStatusCode.OK)
            {
                // Read manifest, rewrite segment URLs through this listener,
                // emit. Cancel the body-deadline timer so a slow manifest
                // doesn't false-trip the idle watchdog (they're small).
                reqCts.CancelAfter(Timeout.InfiniteTimeSpan);
                string manifest = await resp.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);
                string rewritten = HlsManifestRewriter.Rewrite(manifest, targetUrl, _port);
                byte[] manifestBytes = Encoding.UTF8.GetBytes(rewritten);
                ctx.Response.ContentLength64 = manifestBytes.Length;
                try { ctx.Response.Headers["Content-Type"] = "application/vnd.apple.mpegurl"; } catch { }
                await ctx.Response.OutputStream.WriteAsync(manifestBytes, _cts.Token).ConfigureAwait(false);
                return;
            }

            if (contentLength.HasValue) ctx.Response.ContentLength64 = contentLength.Value;

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
            }
        }
        catch (HttpListenerException) { /* AVPro disconnected mid-stream; expected */ }
        catch (System.IO.IOException) { /* same */ }
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

    private static void ForwardRequestHeaders(HttpListenerRequest src, HttpRequestMessage dst)
    {
        // Range is the critical one for HLS segments + seek operations.
        // Also forward If-* validators so caching upstream behaves.
        // Drop Host (HttpClient sets its own from the URL) and Connection
        // (HTTP/1.1 vs /2 confusion). Drop Cookie/Authorization -- the
        // server already issued the URL for THIS player; client-side cookies
        // would be irrelevant or actively wrong (we don't impersonate AVPro
        // here, the upstream is an already-resolved URL with whatever auth
        // is baked into its query string).
        foreach (string? key in src.Headers.AllKeys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            string? value = src.Headers[key];
            if (value == null) continue;
            // Range, If-*, User-Agent, Accept, Accept-Encoding, etc.
            if (!dst.Headers.TryAddWithoutValidation(key, value))
            {
                // Some headers go on Content; for GET there is no content,
                // so we don't fight it.
            }
        }
    }

    private static bool LooksLikeHls(string contentType, string url)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            if (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) return true;
            if (contentType.Contains("m3u8", StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string DecodeTargetParam(string targetParam)
    {
        // base64url variant: +/ -> -_, no padding. Restore + repad before
        // standard FromBase64String.
        string b64 = targetParam.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        try
        {
            byte[] bytes = Convert.FromBase64String(b64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }

    public static string EncodeTargetParam(string url)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(url);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
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

// HLS manifest rewriter. Rewrites every segment URL (absolute or relative
// resolved against the manifest's base URL) to route through this listener
// via /play?target=<base64-of-resolved-segment-url>.
[SupportedOSPlatform("windows")]
internal static partial class HlsManifestRewriter
{
    [GeneratedRegex(@"URI=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex UriAttributeRegex();

    public static string Rewrite(string manifest, string baseUrl, int port)
    {
        if (string.IsNullOrEmpty(manifest)) return manifest;

        Uri? baseUri = null;
        Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri);
        string portStr = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var sb = new StringBuilder(manifest.Length + 256);
        foreach (string rawLine in manifest.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (line.Length == 0)
            {
                sb.Append('\n');
                continue;
            }

            // Tag lines: rewrite any URI="..." attribute they carry.
            // EXT-X-MAP, EXT-X-MEDIA, EXT-X-I-FRAME-STREAM-INF, etc.
            if (line.StartsWith('#'))
            {
                string rewrittenTag = UriAttributeRegex().Replace(line, m =>
                {
                    string segUrl = m.Groups[1].Value;
                    string resolved = ResolveAgainstBase(baseUri, segUrl);
                    string wrapped = "http://localhost.youtube.com:" + portStr
                        + "/play?target=" + LocalRelayServer.EncodeTargetParam(resolved);
                    return "URI=\"" + wrapped + "\"";
                });
                sb.Append(rewrittenTag);
                sb.Append('\n');
                continue;
            }

            // Bare URI line (segment URL or sub-playlist URL). Rewrite as a
            // whole. Anything that doesn't parse as a URL when resolved
            // against the manifest URI is left alone (defensive: don't
            // mangle weird manifest content).
            string trimmed = line.Trim();
            string segResolved = ResolveAgainstBase(baseUri, trimmed);
            if (Uri.TryCreate(segResolved, UriKind.Absolute, out _))
            {
                sb.Append("http://localhost.youtube.com:");
                sb.Append(portStr);
                sb.Append("/play?target=");
                sb.Append(LocalRelayServer.EncodeTargetParam(segResolved));
                sb.Append('\n');
            }
            else
            {
                sb.Append(line);
                sb.Append('\n');
            }
        }

        // The split-on-\n + per-line append-\n loop always produces ONE
        // extra trailing \n vs the input. Strip it so the rewriter is
        // byte-identical on no-op input AND preserves the original's
        // newline shape on rewrites. (input "a" -> ["a"] -> "a\n" -> "a";
        // input "a\n" -> ["a",""] -> "a\n\n" -> "a\n".)
        if (sb.Length > 0 && sb[^1] == '\n') sb.Length -= 1;
        return sb.ToString();
    }

    private static string ResolveAgainstBase(Uri? baseUri, string maybeRelative)
    {
        if (Uri.TryCreate(maybeRelative, UriKind.Absolute, out _))
            return maybeRelative;
        if (baseUri == null) return maybeRelative;
        if (Uri.TryCreate(baseUri, maybeRelative, out var resolved))
            return resolved.ToString();
        return maybeRelative;
    }
}
