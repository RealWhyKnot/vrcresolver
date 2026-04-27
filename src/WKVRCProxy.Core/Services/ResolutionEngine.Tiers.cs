using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.IPC;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;
// Partial — tier execution. AttemptTier1/2/3 are the cascade's call sites; ResolveTierN is the
// individual implementation. RunTier1Attempt is the workhorse yt-dlp variant invoker behind the
// cold-race strategies. Streamlink lives here because it shares the yt-dlp subprocess plumbing.
[SupportedOSPlatform("windows")]
public partial class ResolutionEngine
{
    // --- moved from ResolutionEngine.cs (lines 1303-1351) ---
    private async Task<YtDlpResult?> AttemptTier1(string url, string player, RequestContext ctx)
    {
        var (res, ms) = await TimedResolve(() => ResolveTier1(url, player, ctx));
        if (res == null)
        {
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] yt-dlp returned no URL after " + ms + "ms â€” check stderr above for cause.");
            return null;
        }
        if (!await CheckUrlReachable(res.Url, ctx))
        {
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] URL resolved in " + ms + "ms but failed reachability check â€” cascading to next tier.");
            return null;
        }
        _logger.Info("[" + ctx.CorrelationId + "] [Tier 1] Success in " + ms + "ms" + FormatMetaLog(res) + ".");
        return res;
    }

    private async Task<YtDlpResult?> AttemptTier2(string url, string player, RequestContext ctx)
    {
        var (res, ms) = await TimedResolve(() => ResolveTier2(url, player, ctx));
        if (res == null)
        {
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 2] Cloud resolver returned no URL after " + ms + "ms.");
            return null;
        }
        _logger.Info("[" + ctx.CorrelationId + "] [Tier 2] Success in " + ms + "ms" + FormatMetaLog(res) + ".");
        return res;
    }

    private async Task<YtDlpResult?> AttemptTier3(string[] originalArgs, RequestContext ctx)
    {
        var (res, ms) = await TimedResolve(() => ResolveTier3(originalArgs, ctx));
        if (res == null)
        {
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 3] yt-dlp-og returned no URL after " + ms + "ms.");
            return null;
        }
        _logger.Info("[" + ctx.CorrelationId + "] [Tier 3] Success in " + ms + "ms" + FormatMetaLog(res) + ".");
        return res;
    }

    // Builds the set of strategies to race in parallel on a cold-start request (no StrategyMemory
    // hit). The catalog is request-aware: YouTube URLs get the PO-token variant; non-YouTube URLs
    // get the impersonate-only and vrchat-ua variants (aimed at movie-world hosts). Tier 2 is always
    // included because it runs on a WebSocket (no subprocess).
    // Rate-limit helpers: prevent the cold race from firing more than
    // AppConfig.PerHostRequestBudget yt-dlp processes per AppConfig.PerHostRequestWindowSeconds
    // against the same host. Match yt-dlp maintainer guidance of â‰¤2â€“3 concurrent requests per
    // origin. Cloud (tier 2) is exempt â€” it hits whyknot.dev from a different IP.

    // --- moved from ResolutionEngine.cs (lines 1615-1656) ---
    private async Task<YtDlpResult?> ResolveTier1(string url, string player, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1] Attempting native yt-dlp resolution...");

        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");
        string host = ExtractHost(url);
        string? videoId = isYouTube ? ExtractYouTubeVideoId(url) : null;

        // Decide whether to fetch a PO token up front. YouTube doesn't require PO on every request â€”
        // it flips into bot-detection mode domain-wide for a window of ~30 min. The fast-path (no PO)
        // completes in 2-3s when YouTube is happy; PO token fetch adds 5-15s. So: only pay the PO cost
        // when we've recently seen a bot-check for this host.
        bool needsPot = isYouTube && DomainRequiresPot(host);
        var result = await RunTier1Attempt(url, player, ctx, injectPot: needsPot, videoId);

        // Fast-path failure mode: bot-check stderr even though we didn't send a PO token. Flag the
        // domain so the next request uses PO upfront. Don't retry in-call â€” the cascade falls through
        // to Tier 2 for this request; next Tier 1 call will take the PO path and likely succeed.
        if (result == null && !needsPot && isYouTube && IsBotDetectionStderr(_lastTier1Stderr))
        {
            MarkDomainRequiresPot(host);
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] YouTube bot detection triggered on fast-path for '" + host + "' â€” flagging domain for PO token for " + DomainRequiresPotTtl.TotalMinutes + " min.");
        }
        // PO-path failure: PO token was injected but bot-check still fired. Refresh the flag so we keep
        // using PO, and log loudly â€” this usually means the bgutil sidecar's token is stale.
        else if (result == null && needsPot && isYouTube && IsBotDetectionStderr(_lastTier1Stderr))
        {
            MarkDomainRequiresPot(host);
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] YouTube bot detection triggered EVEN WITH PO token for '" + host + "' â€” check bgutil sidecar health.");
        }

        return result;
    }

    // Stashed stderr from the most recent Tier 1 attempt so the outer method can decide whether to
    // flag the domain. Avoids changing RunYtDlp's signature just to plumb stderr through one path.
    private string _lastTier1Stderr = "";

    // Browser-extract executor. Runs a headless browser, captures the first media URL it sees,
    // probes whether AVPro can reach it directly, and (if not) caches the session headers/cookies
    // in BrowserSessionCache for the relay to replay. Returns a YtDlpResult wrapping the media URL.
    // The strategy's ForceRelayWrap flag tells ApplyRelayWrap to wrap the URL even for non-YouTube

    // --- moved from ResolutionEngine.cs (lines 1658-1859) ---
    private async Task<YtDlpResult?> RunBrowserExtract(string url, RequestContext ctx, CancellationToken ct)
    {
        if (_browserExtractor == null)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [browser-extract] Service not wired â€” strategy skipped.");
            return null;
        }
        if (!_settings.Config.EnableBrowserExtract)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [browser-extract] Disabled by config (EnableBrowserExtract=false).");
            return null;
        }

        // Deadline: 25s gives the browser enough time to load and intercept a first manifest while
        // still letting faster strategies win the race. Site load typically lands in 3â€“8s.
        var sw = Stopwatch.StartNew();
        var result = await _browserExtractor.ExtractMediaUrlAsync(url, TimeSpan.FromSeconds(25), ct);
        sw.Stop();

        if (result == null)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [browser-extract] No media URL captured in " + sw.ElapsedMilliseconds + "ms.");
            return null;
        }
        _logger.Info("[" + ctx.CorrelationId + "] [browser-extract] Captured media URL in " + result.ElapsedMs + "ms (" + result.RequestsLogged + " requests seen, sessionCached=" + result.SessionCached + ").");
        return new YtDlpResult(result.MediaUrl, result.Height, null, null, null, null);
    }

    private Task<YtDlpResult?> RunTier1Attempt(string url, string player, RequestContext ctx, bool injectPot, string? videoId)
        => RunTier1Attempt(url, player, ctx, injectPot, injectImpersonate: _curlClient?.IsAvailable == true, userAgent: null, referer: null, videoId: videoId, variantLabel: "default", playerClient: null);

    // Variant-aware Tier 1 yt-dlp invocation. Strategies in the catalog call through this with
    // different flag combinations so the dispatcher can race them in parallel. The variantLabel
    // shows up in log lines for diagnostic clarity.
    //
    // playerClient: when non-null, passes --extractor-args youtube:player_client=<value>. yt-dlp
    // supports 'web', 'mweb', 'ios', 'ios_music', 'android_vr', 'tv_embedded', 'web_safari', etc.
    // Different clients return different format sets and have different bot-detection profiles â€”
    // some survive restrictive-mode/age-gating where the default 'web' client fails. Combining
    // multiple clients in --extractor-args is legal (comma-separated); we keep one per strategy
    // so the memory ranker can learn which specific client wins per host.
    private async Task<YtDlpResult?> RunTier1Attempt(string url, string player, RequestContext ctx,
        bool injectPot, bool injectImpersonate, string? userAgent, string? referer, string? videoId, string variantLabel, string? playerClient = null, bool useWarp = false, bool forceIpv6 = false)
    {
        // --print replaces legacy --get-url and lets us capture format metadata (height/vcodec) on the side.
        // Two sentinel-prefixed lines are emitted so the parser can distinguish URL from meta line.
        var args = new List<string> {
            "--print", "url:%(url)s",
            "--print", "meta:%(height)s|%(width)s|%(vcodec)s|%(format_id)s|%(protocol)s",
            "--no-warnings", "--playlist-items", "1"
        };
        // forceIpv6 wins over ForceIPv4 when the ipv6 strategy is explicitly selected â€” the whole
        // point of that variant is to route around v4 rate limits. If v6 connectivity is missing,
        // yt-dlp surfaces a network error and the strategy records a normal failure.
        if (forceIpv6)
        {
            args.Add("--force-ipv6");
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] Forcing IPv6 egress (v4 rate-limit bypass).");
        }
        else if (_settings.Config.ForceIPv4) args.Add("--force-ipv4");

        // JS runtime + EJS challenge solver: modern YouTube signs stream URLs via JS challenges.
        // Without a JS runtime registered, yt-dlp prints "Signature solving failed" / "n challenge
        // solving failed" and drops every SABR-guarded format, ending in "Only images are
        // available" even when the PO token flow succeeded. Deno ships next to yt-dlp.exe (see
        // build.ps1). --remote-components ejs:github lets yt-dlp fetch the challenge solver
        // script at request time; it caches thereafter.
        string denoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "deno.exe");
        if (File.Exists(denoPath))
        {
            args.Add("--js-runtimes");
            args.Add("deno:" + denoPath);
            args.Add("--remote-components");
            args.Add("ejs:github");
        }

        // Cloudflare WARP route-through: yt-dlp (and the generic extractor's HTTP probes) go out via
        // our on-host wireproxy SOCKS5 listener, which is user-space WG to the Cloudflare edge. Only
        // this specific yt-dlp subprocess is affected â€” nothing else on the host routes through WARP.
        //
        // Two ways to opt in:
        //   - useWarp=true        â€” strategy-level (the warp+ variants pass this).
        //   - Config.MaskIp=true  â€” global; every tier-1 yt-dlp call routes through WARP regardless
        //                           of which variant is firing.
        //
        // EnsureRunningAsync lazily starts wireproxy on first call (subsequent calls are O(1)). If
        // WARP genuinely can't start (binaries missing, port collision, wgcf failure), the strategy
        // fails outright rather than silently falling back to direct â€” otherwise a Mask-IP user
        // would think their IP is masked while it's actually leaking, and the cold-race winner
        // would look like "warp+default" while in reality doing exactly what tier1:default would.
        bool effectiveUseWarp = useWarp || _settings.Config.MaskIp;
        if (effectiveUseWarp)
        {
            if (_warp == null || !await _warp.EnsureRunningAsync())
            {
                string reason = _settings.Config.MaskIp && !useWarp
                    ? "Mask IP is on but WARP is unavailable (" + (_warp?.StatusDetail ?? "service not registered") + ") â€” refusing to leak real IP. Turn Mask IP off in Settings if WARP isn't usable on this machine."
                    : "WARP unavailable (" + (_warp?.StatusDetail ?? "service not registered") + ") â€” strategy aborted. Disable warp+ strategies in Settings if WARP isn't usable on this machine.";
                _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] " + reason);
                return null;
            }
            args.Add("--proxy");
            args.Add(_warp.SocksProxyUrl);
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] Routing yt-dlp through WARP SOCKS5 (" + _warp.SocksProxyUrl + (useWarp ? ", strategy-level" : ", Mask IP global") + ").");
        }

        if (injectPot)
        {
            // Hand PO resolution off to the bgutil yt-dlp plugin: yt-dlp calls the sidecar at request
            // time and receives a PO token bound to yt-dlp's own visitor_data, which is what YouTube
            // actually validates against. The previous manual-fetch path passed a token bound to
            // a fake visitor_data string, so YouTube rejected it and every Tier 1 strategy fell
            // through to Tier 2. Plugin path mirrors WhyKnot.dev's server-side wiring, minus cookies.
            string pluginDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "yt-dlp-plugins");
            if (_potProvider == null || _potProvider.Port <= 0)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] PotProviderService not ready â€” skipping PO hookup.");
            }
            else if (!Directory.Exists(Path.Combine(pluginDir, "bgutil-ytdlp-pot-provider", "yt_dlp_plugins")))
            {
                _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] bgutil plugin dir missing at '" + pluginDir + "' â€” yt-dlp will run without PO support.");
            }
            else
            {
                args.AddRange(BuildBgutilPluginArgs(pluginDir, _potProvider.Port));
                _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] bgutil plugin enabled (sidecar port " + _potProvider.Port + ").");
            }
        }
        else
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] Skipping PO token fetch.");
        }

        if (!string.IsNullOrEmpty(userAgent))
        {
            args.Add("--user-agent");
            args.Add(userAgent);
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] User-Agent override: " + userAgent);
        }
        if (!string.IsNullOrEmpty(referer))
        {
            args.Add("--referer");
            args.Add(referer);
        }

        string formatStr;
        string res = _settings.Config.PreferredResolution.Replace("p", "");
        if (player == "AVPro")
        {
            // AVPro supports HLS, DASH, and MP4. Prefer HLS first (works for both live and VOD).
            // Height-capped branches are tried first so AVPro does not choke on 4K / HEVC it cannot decode;
            // unrestricted fallbacks keep us from ever returning nothing when only higher renditions exist.
            formatStr = "best[protocol^=m3u8_native][height<=" + res + "]/"
                      + "best[protocol^=http_dash_segments][height<=" + res + "]/"
                      + "best[ext=mp4][height<=" + res + "]/"
                      + "best[protocol^=m3u8_native]/"
                      + "best[ext=mp4]/bestaudio/best";
        }
        else
        {
            // Unity player: progressive HTTP MP4 only. yt-dlp's `protocol^=http` matches `http`
            // and `https` but NOT `http_dash_segments` or `m3u8_native`, so this filters out
            // DASH and HLS that Unity silently chokes on. Matches VRChat's own native yt-dlp
            // selector ((mp4/best)[protocol^=http]) â€” copying the one yt-dlp knows Unity can
            // actually play. bestaudio at the tail keeps audio-only hosts resolvable.
            formatStr = "best[protocol^=http][ext=mp4][height<=" + res + "]/"
                      + "best[protocol^=http][ext=mp4]/"
                      + "best[protocol^=http][height<=" + res + "]/"
                      + "best[protocol^=http]/"
                      + "bestaudio[protocol^=http]/"
                      + "best";
        }
        args.Add("-f");
        args.Add(formatStr);
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] Player=" + player + " Format=" + formatStr);

        // Inject generic:impersonate when curl-impersonate is available.
        // Required for CDN URLs protected by Cloudflare anti-bot (e.g. imvrcdn.com) â€” without this
        // yt-dlp's generic extractor gets HTTP 403 and fails. The youtube extractor ignores this arg.
        if (injectImpersonate && _curlClient?.IsAvailable == true)
        {
            args.Add("--extractor-args");
            args.Add("generic:impersonate");
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] Injecting generic:impersonate.");
        }

        // Per-strategy YouTube player_client override. yt-dlp accepts multiple --extractor-args for
        // the same extractor; they merge at parse time, so this is additive to any po_token flag above.
        if (!string.IsNullOrEmpty(playerClient))
        {
            args.Add("--extractor-args");
            args.Add("youtube:player_client=" + playerClient);
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] YouTube player_client=" + playerClient + ".");
        }

        args.Add(url);
        var (result, stderr) = await RunYtDlp("yt-dlp.exe", args, ctx);
        _lastTier1Stderr = stderr;
        return result;
    }

    // Extract a stable cache key from a YouTube URL.

    // --- moved from ResolutionEngine.cs (lines 1919-1972) ---
    private async Task<YtDlpResult?> ResolveTier2(string url, string player, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 2] Calling WhyKnot.dev via WebSocket...");
        int maxHeight = ParsePreferredHeight();
        string? resolved = await _tier2Client.ResolveUrlAsync(url, player, maxHeight, ctx.CorrelationId);
        // Tier 2 server currently returns only the stream URL. Height stays null until whyknot.dev
        // adds format metadata to the resolve_result message (see follow-up in plan).
        return resolved == null ? null : new YtDlpResult(resolved, null, null, null, null, null);
    }

    private Task<YtDlpResult?> ResolveTier3(string[] originalArgs, RequestContext ctx)
        => ResolveTier3(originalArgs, ctx, userAgent: null, referer: null, variantLabel: "plain");

    private async Task<YtDlpResult?> ResolveTier3(string[] originalArgs, RequestContext ctx,
        string? userAgent, string? referer, string variantLabel)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 3:" + variantLabel + "] Attempting VRChat's yt-dlp-og.exe.");
        var args = originalArgs.ToList();
        if (!string.IsNullOrEmpty(userAgent))
        {
            args.Add("--user-agent");
            args.Add(userAgent);
        }
        if (!string.IsNullOrEmpty(referer))
        {
            args.Add("--referer");
            args.Add(referer);
        }
        // Mask IP applies here too â€” tier 3 is the last-resort fallback, and we don't want it
        // leaking the real IP after the user opted into IP masking. Same loud-fail behavior as
        // tier 1: if WARP can't start, abort rather than silently going direct.
        if (_settings.Config.MaskIp)
        {
            if (_warp == null || !await _warp.EnsureRunningAsync())
            {
                _logger.Warning("[" + ctx.CorrelationId + "] [Tier 3:" + variantLabel + "] Mask IP is on but WARP is unavailable (" + (_warp?.StatusDetail ?? "service not registered") + ") â€” refusing to leak real IP through yt-dlp-og.");
                return null;
            }
            args.Add("--proxy");
            args.Add(_warp.SocksProxyUrl);
        }
        var (result, _) = await RunYtDlp("yt-dlp-og.exe", args, ctx);
        return result;
    }

    // Asks Streamlink whether it has a plugin that handles the given URL.
    // Uses `streamlink --can-handle-url <url>` which is a local plugin registry check â€”
    // no network call, completes in <500ms. Exit code 0 means Streamlink supports the URL;
    // non-zero means it doesn't. This is the authoritative gate for Tier 0: no hardcoded
    // domain lists, no URL pattern matching â€” Streamlink's own registry decides.
    //
    // Results are cached per-host to avoid paying ~500ms on every resolve for the same unsupported
    // domain (e.g. vr-m.net). Plugin list only changes across Streamlink upgrades, so 24h/7d TTLs
    // are plenty.

    // --- moved from ResolutionEngine.cs (lines 1973-2055) ---
    private async Task<bool> StreamlinkCanHandleUrlAsync(string url, RequestContext ctx)
    {
        string path = GetBinaryPath("streamlink.exe");
        if (!File.Exists(path)) return false; // Not installed â€” skip silently

        string host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : url;
        if (_streamlinkCapabilityCache.TryGetValue(host, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Streamlink capability cache hit for " + host + " â†’ " + cached.CanHandle + ".");
            if (!cached.CanHandle)
                _logger.Info("[" + ctx.CorrelationId + "] [Tier 0] Streamlink has no plugin for " + host + " â€” skipping (cached).");
            return cached.CanHandle;
        }
        bool result = await StreamlinkCanHandleUrlUncachedAsync(url, path, ctx);
        var ttl = result ? StreamlinkCacheTtlPositive : StreamlinkCacheTtlNegative;
        _streamlinkCapabilityCache[host] = (result, DateTime.UtcNow.Add(ttl));
        if (!result)
            _logger.Info("[" + ctx.CorrelationId + "] [Tier 0] Streamlink has no plugin for " + host + " â€” skipping.");
        return result;
    }

    private async Task<bool> StreamlinkCanHandleUrlUncachedAsync(string url, string path, RequestContext ctx)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = path;
            process.StartInfo.ArgumentList.Add("--can-handle-url");
            process.StartInfo.ArgumentList.Add(url);
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            ProcessGuard.Register(process);

            // Drain stdout/stderr to prevent buffer deadlock â€” exit code is all we need.
            // Suppress ObjectDisposedException if the process is killed on the timeout path.
            _ = process.StandardOutput.ReadToEndAsync().ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            _ = process.StandardError.ReadToEndAsync().ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);

            var tcs = new TaskCompletionSource<int>();
            _ = Task.Run(() => {
                try { process.WaitForExit(); tcs.TrySetResult(process.ExitCode); }
                catch (ObjectDisposedException) { tcs.TrySetResult(-1); }
                catch (InvalidOperationException) { tcs.TrySetResult(-1); }
            });

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            if (completed != tcs.Task)
            {
                try { process.Kill(); } catch { }
                _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] --can-handle-url timed out â€” skipping Streamlink.");
                return false;
            }

            return await tcs.Task == 0;
        }
        catch (Exception ex)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] --can-handle-url error: " + ex.Message);
            return false;
        }
    }

    private async Task<YtDlpResult?> ResolveStreamlink(string url, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Attempting Streamlink resolution...");
        var args = new List<string> { "--stream-url", "--quiet" };
        // When opted in, ask Streamlink to filter Twitch ad segments. AVPro will stall on the last
        // good frame for the duration of the ad break (no time-skip); ads themselves are not shown.
        // Default off â€” ads pass through and play, no pause.
        if (_settings.Config.StreamlinkDisableTwitchAds && url.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--twitch-disable-ads");
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Twitch ad filter ON â€” playback will stall during ad breaks.");
        }
        args.Add(url);
        args.Add("best");
        var (result, _) = await RunYtDlp("streamlink.exe", args, ctx, timeoutMs: 9000);
        return result;
    }

}
