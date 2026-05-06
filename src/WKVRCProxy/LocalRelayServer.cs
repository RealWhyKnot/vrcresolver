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
                if (ShouldDropResponseHeader(h.Key)) continue;
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

    // Upstream response headers that MUST NOT be re-emitted to AVPro. The
    // trust-gateway listener pretends to be `localhost.youtube.com:{port}`,
    // a hostname AVPro considers trusted. The actual upstream is Cloudflare
    // in front of node1.whyknot.dev, so the response carries CF-specific
    // headers (Alt-Svc, CF-RAY, cf-cache-status, Speculation-Rules,
    // Report-To, Nel) that don't belong on a hostname AVPro thinks is on
    // its own trust list. `Alt-Svc: h3=":443"` in particular tells AVPro
    // to upgrade to HTTP/3 on port 443 of localhost.youtube.com -- nothing
    // is listening there, the upgrade fails, AVPro may bail.
    //
    // Drop them before re-emitting. Transfer-Encoding gets the same
    // treatment because HttpListener manages its own framing.
    private static bool ShouldDropResponseHeader(string name)
    {
        if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "Alt-Svc", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "Server", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "cf-cache-status", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "Speculation-Rules", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "Report-To", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, "Nel", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("CF-", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
// via /play?target=<base64-of-resolved-segment-url> -- UNLESS the segment's
// real upstream is already on AVPro's trusted-host allowlist (googlevideo,
// vimeo, etc.), in which case the segment URL is emitted directly.
//
// The unwrap path closes a manifest-size blowup that broke long-form video
// playback. The server's RewriteHls wraps every segment through
// `node1.whyknot.dev/api/proxy?url=<base64-of-segment>` for WARP egress +
// CF caching benefits. The listener used to wrap THAT again through
// `localhost.youtube.com:port/play?target=<base64-of-node1-url>` to make
// AVPro's trust check pass on every segment fetch. The double-encoding
// inflates each segment URL ~2x; for a 2.7-hour video with ~1700
// segments the manifest grows to ~4.9 MB. AVPro can't handle that and
// times out at ~2 seconds with `Loading failed`, world script re-fires,
// stuck loop.
//
// The fix: when a segment URL is the server's `node1/api/proxy?url=<base64>`
// shape, decode the inner URL and check the host. If the host is on
// AVPro's built-in trusted allowlist (which is where YouTube segments
// already live -- *.googlevideo.com), emit the inner URL directly. AVPro's
// trust check still passes (googlevideo is allowlisted), the manifest stays
// small, segments fetch directly from googlevideo. Tradeoff: bypass the
// server's per-segment proxy (lose CF edge cache + WARP egress on segment
// bytes). Acceptable: CF Page Rule isn't activated yet, and YouTube doesn't
// IP-block playback.
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
                    string emitted = ResolveSegmentForEmit(resolved, portStr);
                    return "URI=\"" + emitted + "\"";
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
                sb.Append(ResolveSegmentForEmit(segResolved, portStr));
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

    // Decide what URL to emit for a segment. Returns either:
    //   1. The trusted upstream URL directly. Two paths into this branch:
    //      a) Input is the server's `node1.whyknot.dev/api/proxy?url=<base64>`
    //         shape AND the inner URL's host is on AVPro's trust list -- we
    //         unwrap and emit the inner URL.
    //      b) Input is already a trust-list-passing URL (e.g. a direct
    //         googlevideo segment) -- we emit it as-is.
    //      Both avoid the manifest-size blowup that broke long-form playback.
    //   2. The trust-gateway wrap (otherwise). Standard path for hosts that
    //      aren't already trust-list-passing.
    internal static string ResolveSegmentForEmit(string resolvedSegmentUrl, string portStr)
    {
        string? bypassUrl = TryUnwrapServerProxyToTrustedHost(resolvedSegmentUrl);
        if (bypassUrl != null) return bypassUrl;

        if (Uri.TryCreate(resolvedSegmentUrl, UriKind.Absolute, out var direct)
            && TrustedAvProHosts.IsTrusted(direct.Host))
        {
            return resolvedSegmentUrl;
        }

        return "http://localhost.youtube.com:" + portStr
            + "/play?target=" + LocalRelayServer.EncodeTargetParam(resolvedSegmentUrl);
    }

    // Match the server's `https://node{N}.whyknot.dev/api/proxy?url=<base64>`
    // shape. If the inner URL's host is on AVPro's allowlist, return the
    // inner URL so AVPro can fetch the segment directly. Returns null when
    // the segment can't safely be unwrapped (host not allowlisted, parse
    // failure, malformed base64, etc.) -- caller falls through to wrap.
    private static string? TryUnwrapServerProxyToTrustedHost(string segmentUrl)
    {
        if (!Uri.TryCreate(segmentUrl, UriKind.Absolute, out var uri)) return null;
        // Server's proxy host pattern: node{N}.whyknot.dev (case-insensitive).
        // Path must be exactly /api/proxy; query must contain `url=<base64>`.
        if (!uri.Host.EndsWith(".whyknot.dev", StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.AbsolutePath.Equals("/api/proxy", StringComparison.OrdinalIgnoreCase)) return null;

        // Pull the `url=` query param. Server uses it for segments (vs `q=`
        // for full-state-pack manifest URLs).
        string? urlParam = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            string k = part.Substring(0, eq);
            if (k.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                urlParam = part.Substring(eq + 1);
                break;
            }
        }
        if (string.IsNullOrEmpty(urlParam)) return null;

        // Server emits standard base64 (with padding) for `?url=`. URL-decode
        // first in case the manifest URL-encoded any characters along the way.
        string decoded;
        try
        {
            string urlUnescaped = Uri.UnescapeDataString(urlParam);
            string b64 = urlUnescaped.Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
            }
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch { return null; }

        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var inner)) return null;
        if (!TrustedAvProHosts.IsTrusted(inner.Host)) return null;
        return decoded;
    }
}

// VRChat AVPro's built-in trusted-host allowlist. Hosts matching one of
// these patterns pass AVPro's trust check in default-public worlds without
// needing the localhost.youtube.com gateway wrap. Last verified against
// VRChat: 2026-04-23 (project_vrchat_trusted_url_list memory). The list
// evolves slowly; if VRChat adds providers we add them here.
//
// Used by HlsManifestRewriter to decide when a segment URL can be emitted
// directly (manifest stays small) vs needing the gateway wrap (larger but
// trust-list-passing for non-allowlisted hosts).
[SupportedOSPlatform("windows")]
internal static class TrustedAvProHosts
{
    private static readonly string[] s_exactHosts = new[]
    {
        "youtu.be",
        "soundcloud.com",
        "vod-progressive.akamaized.net",
    };

    // Wildcard suffixes: host matches if it is exactly the suffix OR ends
    // with `.<suffix>`. So `*.youtube.com` matches `youtube.com`,
    // `www.youtube.com`, and `m.youtube.com` but not `notyoutube.com`.
    private static readonly string[] s_suffixes = new[]
    {
        "youtube.com",
        "googlevideo.com",
        "facebook.com",
        "fbcdn.net",
        "hyperbeam.com",
        "hyperbeam.dev",
        "mixcloud.com",
        "nicovideo.jp",
        "sndcdn.com",
        "topaz.chat",
        "twitch.tv",
        "ttvnw.net",
        "twitchcdn.net",
        "vrcdn.live",
        "vrcdn.video",
        "vrcdn.cloud",
        "vimeo.com",
        "youku.com",
    };

    public static bool IsTrusted(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        foreach (var h in s_exactHosts)
            if (host.Equals(h, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var sfx in s_suffixes)
        {
            if (host.Equals(sfx, StringComparison.OrdinalIgnoreCase)) return true;
            if (host.Length > sfx.Length + 1
                && host.EndsWith("." + sfx, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
