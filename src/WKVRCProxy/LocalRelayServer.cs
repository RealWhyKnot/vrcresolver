using System.Collections.Concurrent;
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
//   http://localhost.youtube.com:{port}/play?target=<base64>   (manifest URL,
//   http://localhost.youtube.com:{port}/play?id=<12hex>         emitted by wrapper)
//                                                              (segment URL,
//                                                              emitted by the
//                                                              manifest rewriter
//                                                              into the manifest
//                                                              this listener
//                                                              returns to AVPro)
// AVPro's trust check passes on either form; the listener resolves the URL
// (decode for `target=`, lookup for `id=`) and forwards to the real upstream
// (typically `https://node1.whyknot.dev/api/proxy?...`) and streams bytes back.
//
// EVERY byte routes through whyknot.dev. Both the initial manifest fetch
// (where the wrapper-emitted target= URL points at the server proxy) and
// every segment fetch (where the rewriter-emitted id= maps to the server
// proxy URL the manifest pulled in). This preserves WARP egress, central
// control, and CF caching potential for all bytes.
//
// HLS handling: when the upstream Content-Type is application/vnd.apple.mpegurl
// or the URL contains `.m3u8`, the listener reads the manifest as text and
// rewrites every segment URL into a `/play?id=<12hex>` URL via the
// SegmentIdRegistry. The 12-hex encoding compresses the manifest by ~98%
// vs base64-encoding-the-full-server-URL-into-each-line, fixing the
// long-form-video manifest blowup.
//
// Phase 1: HTTP-only. HTTPS + per-machine cert lifecycle is a separate
// follow-up. AVPro accepts plain http:// on a non-443 port for hostnames
// matching its allowlist (legacy implementation proved this for years).
//
// AOT-clean: HttpListener + HttpClient + System.IO + GeneratedRegex +
// ConcurrentDictionary + RandomNumberGenerator. No reflection, no dynamic codegen.
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
    private readonly SegmentIdRegistry _idRegistry = new();
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
        // AutomaticDecompression = All is load-bearing. Cloudflare gzip-encodes
        // application/vnd.apple.mpegurl when the forwarded request advertises
        // Accept-Encoding: gzip (the in-game player does, and we pass that
        // header through). The HLS rewriter MUST run on plaintext: if it ran
        // on gzipped bytes interpreted as UTF-8, output would be garbage
        // re-emitted to the player WITH Content-Encoding: gzip still set,
        // and the player would fail to gunzip plaintext (manifest load_failure).
        // .NET strips Content-Encoding and Content-Length from the response
        // headers when it transparently decompresses, so downstream forwarding
        // stays consistent.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
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
            if (!path.StartsWith("/play", StringComparison.OrdinalIgnoreCase))
            {
                if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " -> 404 (path mismatch)");
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            // Three forms supported, resolved in priority order:
            //
            //   /play/<hex>(.<ext>)?  -- segment URL emitted by
            //     HlsManifestRewriter. The cosmetic <ext> suffix
            //     (.mp4, .ts, .m4s, ...) is the primary signal AVPro/
            //     MediaFoundation uses to pick a byte-stream handler;
            //     without it MF defaults to a single Range bytes=0-
            //     per URL and breaks byterange-addressed fmp4 (Tubi).
            //     The handler ignores the suffix when resolving the
            //     id against SegmentIdRegistry.
            //
            //   /play/manifest.<ext>?target=<base64url>  -- manifest URL
            //     emitted by the wrapper's TryWrapForTrustGateway. The
            //     hex-slot is a literal "manifest" placeholder; the real
            //     resolution comes from target=. <ext> is the upstream
            //     manifest extension (.m3u8, .mpd) so MF dispatches the
            //     HLS / DASH handler.
            //
            //   /play?target=<base64url> or /play?id=<12hex>  -- legacy
            //     query-string forms. Kept for backwards compatibility
            //     with watchdog binaries that pre-date the path form.
            //
            // Priority: target= wins over path-id (so the manifest-
            // placeholder hex doesn't accidentally hit the registry).
            // 404 if id= or path-id is given but unknown -- listener
            // restarted, or the entry rolled off the FIFO. AVPro re-
            // fetches the manifest in that case and gets fresh IDs.
            string? pathId = null;
            if (path.Length > "/play/".Length
                && path.StartsWith("/play/", StringComparison.OrdinalIgnoreCase))
            {
                string after = path.Substring("/play/".Length);
                int dotIdx = after.IndexOf('.');
                pathId = dotIdx >= 0 ? after.Substring(0, dotIdx) : after;
            }
            string? targetParam = ctx.Request.QueryString["target"];
            string? idParam = string.IsNullOrEmpty(targetParam)
                ? (pathId ?? ctx.Request.QueryString["id"])
                : null;
            string kind;
            if (!string.IsNullOrEmpty(idParam))
            {
                string? mapped = _idRegistry.TryGetUrl(idParam);
                if (string.IsNullOrEmpty(mapped))
                {
                    if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " -> 404 (id=" + idParam + " not in registry; size=" + _idRegistry.Count + ")");
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }
                targetUrl = mapped;
                kind = "segment";
            }
            else if (!string.IsNullOrEmpty(targetParam))
            {
                targetUrl = DecodeTargetParam(targetParam);
                kind = "manifest-or-direct";
            }
            else
            {
                if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " -> 400 (no id or target param)");
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }
            if (string.IsNullOrEmpty(targetUrl) || !Uri.TryCreate(targetUrl, UriKind.Absolute, out _))
            {
                if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " -> 400 (invalid resolved target='" + ShortUrl(targetUrl) + "')");
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }
            if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " kind=" + kind + " target=" + ShortUrl(targetUrl));

            using var outboundReq = new HttpRequestMessage(
                new HttpMethod(method), targetUrl);
            ForwardRequestHeaders(ctx.Request, outboundReq);

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
                if (ShouldDropResponseHeader(h.Key)) { droppedHeaders++; continue; }
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

            bool isHls = LooksLikeHls(contentType, targetUrl);
            if (isHls && resp.StatusCode == HttpStatusCode.OK)
            {
                // Read manifest, rewrite segment URLs through this listener,
                // emit. Cancel the body-deadline timer so a slow manifest
                // doesn't false-trip the idle watchdog (they're small).
                reqCts.CancelAfter(Timeout.InfiniteTimeSpan);
                string manifest = await resp.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);
                int regBefore = s_verbose ? _idRegistry.Count : 0;
                string rewritten = HlsManifestRewriter.Rewrite(manifest, targetUrl, _port, _idRegistry);
                byte[] manifestBytes = Encoding.UTF8.GetBytes(rewritten);
                ctx.Response.ContentLength64 = manifestBytes.Length;
                try { ctx.Response.Headers["Content-Type"] = "application/vnd.apple.mpegurl"; } catch { }
                if (s_verbose)
                {
                    Console.WriteLine("[relay] req=" + reqId + " hls-rewrite raw=" + manifest.Length
                        + "B out=" + manifestBytes.Length + "B segs-added=" + (_idRegistry.Count - regBefore)
                        + " reg-size=" + _idRegistry.Count);
                    DumpHeaders("[relay] req=" + reqId + "  H>", ctx.Response.Headers);
                }
                await ctx.Response.OutputStream.WriteAsync(manifestBytes, _cts.Token).ConfigureAwait(false);
                bytesOut = manifestBytes.Length;
                if (s_verbose) Console.WriteLine("[relay] req=" + reqId + " -> " + ctx.Response.StatusCode
                    + " HLS bytes-out=" + bytesOut + " elapsed=" + (Environment.TickCount64 - t0) + "ms");
                return;
            }

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
        // Defensive: AutomaticDecompression normally strips this. If decompression
        // is ever toggled off, dropping here prevents silently lying to the
        // player about a body we just decompressed.
        if (string.Equals(name, "Content-Encoding", StringComparison.OrdinalIgnoreCase)) return true;
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

// HLS manifest rewriter. Every segment URL in the manifest gets routed
// through this listener, which then fetches the upstream and streams bytes
// back to AVPro. The listener IS the trust gateway -- it's how AVPro can
// reach `node1.whyknot.dev/api/proxy?...` URLs even though `node1` is not
// on AVPro's allowlist; AVPro sees `localhost.youtube.com:{port}` (which
// matches `*.youtube.com`) and accepts.
//
// Encoding: each segment URL is registered against the listener's
// SegmentIdRegistry and rewritten to `localhost.youtube.com:{port}/play?id=<12hex>`.
// AVPro fetches the wrapped URL; the handler looks up the ID; the listener
// fetches the registered upstream (which is the server's
// `node1.whyknot.dev/api/proxy?url=<base64>` URL); bytes flow back to AVPro.
// Every byte routes through whyknot.dev -- WARP egress, central control,
// CF caching potential preserved.
//
// Why an ID-map instead of base64-encoding the upstream URL into the
// manifest line: a typical googlevideo HLS segment URL is ~1500 chars;
// the server's wrap adds another ~30%; base64-encoding that for the
// listener wrap inflates again ~30%. Per segment: ~3000 chars. With ~1700
// segments in a 2.7-hour video, the manifest balloons to ~4.9 MB and
// AVPro times out parsing it. The 12-hex ID compresses each segment URL
// to ~50 chars total in the manifest. 1700 segments x 50 chars = ~85 KB;
// well under any AVPro tolerance.
//
// Limitation: the registry is in-memory + ephemeral. A listener restart
// invalidates all outstanding IDs -- AVPro fetches a segment, gets 404,
// the world script re-fires the resolve, fresh manifest with fresh IDs.
// Acceptable: listener restart only happens on watchdog restart.
[SupportedOSPlatform("windows")]
internal static partial class HlsManifestRewriter
{
    [GeneratedRegex(@"URI=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex UriAttributeRegex();

    public static string Rewrite(string manifest, string baseUrl, int port, SegmentIdRegistry registry)
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
                    string emitted = WrapSegmentThroughListener(resolved, portStr, registry);
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
                sb.Append(WrapSegmentThroughListener(segResolved, portStr, registry));
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

    // Register the segment URL against the listener's id-registry and emit
    // the gateway URL. NO bypass paths: every segment routes through the
    // listener so byte traffic stays on the whyknot.dev path. The registry
    // dedupes -- the same upstream URL appearing multiple times in a
    // manifest (rare but possible for byte-range variants) gets the same id.
    // Path-extension allowlist. AVPro/MediaFoundation dispatches its byte-
    // stream handler primarily on the URL path's extension. The relay
    // mirrors the upstream segment's extension onto the wrapped URL so MF
    // picks the correct handler (.mp4 -> fmp4, .ts -> Transport Stream,
    // .m4s -> DASH segment, etc.). The allowlist guards against synthesising
    // an extension MF doesn't recognise (.bin, .dat) which would mis-
    // dispatch onto the wrong handler. When upstream has no extension,
    // emit /play/<hex> (no suffix) and let MF fall back.
    private static readonly HashSet<string> s_allowedPathExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4s", "m4v", "ts", "m3u8", "mpd",
        "webm", "mkv", "mov",
        "mp3", "m4a", "aac", "ogg", "opus", "wav", "flac",
        "vtt", "srt",
    };

    internal static string ExtractPathExtension(string upstreamUrl)
    {
        if (string.IsNullOrEmpty(upstreamUrl)) return "";
        string path;
        try { path = new Uri(upstreamUrl).AbsolutePath; }
        catch { return ""; }
        string ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || ext.Length < 2) return "";
        string trimmed = ext.Substring(1).ToLowerInvariant();
        return s_allowedPathExts.Contains(trimmed) ? trimmed : "";
    }

    internal static string WrapSegmentThroughListener(string resolvedSegmentUrl, string portStr, SegmentIdRegistry registry)
    {
        string ext = ExtractPathExtension(resolvedSegmentUrl);
        string id = registry.GetOrAddId(resolvedSegmentUrl, ext);
        string suffix = string.IsNullOrEmpty(ext) ? "" : ("." + ext);
        return "http://localhost.youtube.com:" + portStr + "/play/" + id + suffix;
    }
}

// In-memory registry mapping short IDs to full upstream URLs, used by the
// HLS manifest rewriter to compact segment URLs in rewritten manifests.
//
// Each upstream URL gets a 12-hex-char (48-bit) ID via cryptographic RNG;
// collision probability at the 100k-entry soft cap is ~negligible (birthday
// at sqrt(2^48) = 16.7M entries). Capacity is bounded by FIFO eviction
// past the hard cap so a long session can't grow the registry forever.
//
// Each entry also carries the upstream URL's path extension (".mp4", ".ts",
// "" for none). The wrapped URL emitted by HlsManifestRewriter mirrors the
// extension onto the path (`/play/<hex><ext>`) so AVPro/MediaFoundation's
// byte-stream-handler dispatch picks the right handler per segment. The
// extension travels with the URL inside the registry so a request whose
// path-id strips the suffix can still recover the upstream's mime hint.
//
// Concurrency: two threads racing to register the same URL get the same
// ID (atomic via ConcurrentDictionary.TryAdd + race-detection rollback).
// Thread that loses the race generates a wasted ID but cleans it up.
//
// AOT-clean: pure managed code, no reflection.
[SupportedOSPlatform("windows")]
internal sealed class SegmentIdRegistry
{
    public const int DefaultSoftCap = 100_000;
    public const int DefaultHardCap = 200_000;

    // 12 hex chars = 48 bits. Collision probability at HardCap entries:
    // n * (n-1) / (2 * 2^48) = 200k * 200k / (2 * 2^48) = ~7e-5. The
    // generator loops on collision below; this is just for context.
    private const int IdHexLength = 12;
    private const int IdRandomBytes = IdHexLength / 2;
    private const int MaxGenerationAttempts = 8;

    internal sealed record SegmentEntry(string Url, string Ext);

    private readonly ConcurrentDictionary<string, SegmentEntry> _idToEntry = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _urlToId = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    private readonly int _softCap;
    private readonly int _hardCap;

    public SegmentIdRegistry()
        : this(DefaultSoftCap, DefaultHardCap) { }

    // Constructor takes caps so tests can drive eviction with small numbers.
    public SegmentIdRegistry(int softCap, int hardCap)
    {
        if (softCap <= 0 || hardCap <= 0)
            throw new ArgumentOutOfRangeException(softCap <= 0 ? nameof(softCap) : nameof(hardCap),
                "Caps must be positive.");
        if (hardCap < softCap)
            throw new ArgumentException("hardCap must be >= softCap.", nameof(hardCap));
        _softCap = softCap;
        _hardCap = hardCap;
    }

    public int Count => _idToEntry.Count;

    // Backwards-compat overload: register a URL without an extension hint.
    public string GetOrAddId(string url) => GetOrAddId(url, "");

    // Returns the existing ID for `url` if already registered, otherwise
    // registers `(url, ext)` with a fresh 12-hex ID and returns that.
    // `ext` is the lowercase extension without the dot (e.g. "mp4", "ts"),
    // or "" when the upstream has no path extension. Thread-safe;
    // concurrent calls with the same URL converge on a single ID. The
    // extension recorded is whichever caller registered first; subsequent
    // calls with a different ext for the same URL keep the original.
    //
    // Throws ArgumentException for null/empty URL (caller bug; the rewriter
    // already filters those before calling). Throws InvalidOperationException
    // in the astronomically-unlikely case of 8 consecutive ID collisions
    // (something is broken with RNG; fail loud rather than spin).
    public string GetOrAddId(string url, string ext)
    {
        if (string.IsNullOrEmpty(url))
            throw new ArgumentException("URL must be non-empty.", nameof(url));
        ext ??= "";

        // Fast path: already registered. Tight loop hot in long manifests
        // (every segment URL hits this).
        if (_urlToId.TryGetValue(url, out var existingId))
            return existingId;

        // Slow path: capacity check then claim. Eviction runs first so a
        // newly-claimed ID isn't evicted by the same call.
        EvictIfOver();

        for (int attempts = 0; attempts < MaxGenerationAttempts; attempts++)
        {
            string newId = GenerateId();
            var entry = new SegmentEntry(url, ext);

            // Claim newId in idToEntry. ConcurrentDictionary.TryAdd is atomic.
            if (!_idToEntry.TryAdd(newId, entry))
                continue; // ID collision (extremely rare); generate another.

            // Now register the reverse. If a concurrent thread won the
            // urlToId slot first, prefer their ID and roll ours back.
            if (_urlToId.TryAdd(url, newId))
            {
                _insertionOrder.Enqueue(newId);
                return newId;
            }

            _idToEntry.TryRemove(newId, out _);
            // Re-read; the concurrent thread's ID is now the canonical one.
            if (_urlToId.TryGetValue(url, out var concurrentId))
                return concurrentId;
            // Concurrent thread's TryAdd succeeded but their idToEntry insert
            // hasn't landed yet -- spin briefly. In practice this window is
            // sub-microsecond; the spin terminates quickly.
        }

        throw new InvalidOperationException(
            "SegmentIdRegistry could not allocate a unique ID after "
            + MaxGenerationAttempts + " attempts. RNG broken or registry "
            + "wildly oversized; investigate before resuming.");
    }

    public string? TryGetUrl(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _idToEntry.TryGetValue(id, out var entry) ? entry.Url : null;
    }

    public SegmentEntry? TryGetEntry(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _idToEntry.TryGetValue(id, out var entry) ? entry : null;
    }

    // Drop oldest entries down to soft cap when the registry has grown
    // past hard cap. Cheap O(over-by) scan; only runs on pages that push
    // past the hard cap, not on every insert.
    private void EvictIfOver()
    {
        int over = _idToEntry.Count - _hardCap;
        if (over <= 0) return;

        int target = _idToEntry.Count - _softCap;
        for (int i = 0; i < target; i++)
        {
            if (!_insertionOrder.TryDequeue(out var oldId)) break;
            if (_idToEntry.TryRemove(oldId, out var entry))
            {
                // Only remove urlToId mapping if it still points at this
                // specific id. Otherwise a concurrent re-registration of
                // the same URL could be wiped.
                _urlToId.TryRemove(new KeyValuePair<string, string>(entry.Url, oldId));
            }
        }
    }

    // Test-only hook so the test suite can verify eviction without filling
    // the registry to 200k entries.
    public void ClearForTesting()
    {
        _idToEntry.Clear();
        _urlToId.Clear();
        while (_insertionOrder.TryDequeue(out _)) { }
    }

    private static string GenerateId()
    {
        Span<byte> bytes = stackalloc byte[IdRandomBytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
