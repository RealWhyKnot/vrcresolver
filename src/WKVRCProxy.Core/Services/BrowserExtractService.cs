using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

// Headless-browser-based bypass strategy. Loads a URL in a real Chromium, watches network
// traffic for media manifest/payload requests (m3u8/mpd/mp4/webm), captures the winning
// request's cookies+headers, and returns the media URL alongside a session token for relay
// replay. Used as a fallback when yt-dlp strategies can't crack a JS-gated site.
//
// Design:
//   - Single long-lived IBrowser (lazy, created on first use, reused for all subsequent requests).
//     Puppeteer page creation is cheap; browser startup is expensive (~2s cold).
//   - Browser resolution order: Edge → Chrome → bundled Chromium (opt-in; requires one-time
//     ~180 MB download via PuppeteerSharp's BrowserFetcher).
//   - Fully headless; no visible window.
//   - Network monitoring via PuppeteerSharp Request/Response events. First URL matching the media
//     regex wins. Cancel remaining navigation on capture.
//   - Cookies + request headers from the winning request are captured and stashed in
//     BrowserSessionCache keyed by origin host. The relay applies them to every AVPro request
//     hitting the same host.
//   - Probe-first: before committing to relay-replay, we fire a HEAD/GET from .NET with an
//     AVPro-style User-Agent. If the origin serves 200, we skip the session cache and return
//     a pristine URL — the browser's extra headers weren't actually needed. If 401/403, we
//     populate the cache and tag the strategy ForceRelayWrap so the dispatcher wraps the URL.

[SupportedOSPlatform("windows")]
public class BrowserExtractService : IDisposable
{
    private readonly Logger _logger;
    private readonly SettingsManager _settings;
    private readonly BrowserSessionCache _sessionCache;
    private readonly HttpClient _probeClient;

    private IBrowser? _browser;
    private readonly SemaphoreSlim _browserInitLock = new(1, 1);
    private string? _resolvedExecutablePath;
    private bool _disposed;

    // Media URL detection. Ordered by quality preference — when multiple matches fire in the same
    // page load we keep the first HLS/DASH manifest over any raw mp4 segment. This list drives both
    // "is this interesting?" and "is this better than what we already captured?".
    private static readonly (string Label, Regex Pattern, int Priority)[] MediaPatterns =
    {
        ("hls",       new Regex(@"\.m3u8(\?|$)",                                        RegexOptions.IgnoreCase | RegexOptions.Compiled), 10),
        ("dash",      new Regex(@"\.mpd(\?|$)",                                         RegexOptions.IgnoreCase | RegexOptions.Compiled), 20),
        ("mp4",       new Regex(@"\.mp4(\?|$)",                                         RegexOptions.IgnoreCase | RegexOptions.Compiled), 30),
        ("webm",      new Regex(@"\.webm(\?|$)",                                        RegexOptions.IgnoreCase | RegexOptions.Compiled), 40),
        ("media-cdn", new Regex(@"(googlevideo\.com/videoplayback|video\.xx\.fbcdn|media\.|/stream/)", RegexOptions.IgnoreCase | RegexOptions.Compiled), 50),
    };

    // Connectivity/telemetry probes that share a host with real media but carry no payload.
    // YouTube/googlevideo emit /generate_204 to pick the closest CDN edge before streaming starts;
    // if we hand AVPro one of these it'll get 0 bytes back and silently fail to load.
    private static readonly Regex NonMediaPathExclusion =
        new(@"/generate_204(\?|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // AVPro-style UA used for probe-first. Same seed value the Tier 1 vrchat-ua strategy uses.
    private const string ProbeUserAgent = "UnityPlayer/2022.3.22f1 (UnityWebRequest/1.0, libcurl/7.84.0-DEV)";

    public BrowserExtractService(Logger logger, SettingsManager settings, BrowserSessionCache sessionCache)
    {
        _logger = logger;
        _settings = settings;
        _sessionCache = sessionCache;
        _probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _probeClient.DefaultRequestHeaders.UserAgent.ParseAdd(ProbeUserAgent);
    }

    public record BrowserExtractResult(
        string MediaUrl,
        int? Height,               // Parsed from HLS/DASH manifest if we can read it, else null
        bool SessionCached,        // true if relay-replay is needed (probe returned 401/403)
        int RequestsLogged,        // for diagnostic logging
        long ElapsedMs
    );

    // Main entry point. Returns the winning media URL or null on timeout/failure.
    public async Task<BrowserExtractResult?> ExtractMediaUrlAsync(string pageUrl, TimeSpan deadline, CancellationToken ct)
    {
        if (_disposed) return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        IBrowser? browser = await GetOrInitBrowserAsync(ct);
        if (browser == null) return null;

        IPage? page = null;
        int requestsLogged = 0;
        (string Url, int Priority, IDictionary<string, string> Headers)? winner = null;

        try
        {
            page = await browser.NewPageAsync();

            // Use a realistic desktop viewport; some sites serve mobile variants that don't emit
            // the media URL we want.
            await page.SetViewportAsync(new ViewPortOptions { Width = 1920, Height = 1080 });

            var capturedCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(capturedCts.Token, ct);

            page.Request += (_, e) =>
            {
                var req = e.Request;
                var hdrs = req.Headers ?? new Dictionary<string, string>();
                if (requestsLogged < 200)
                {
                    _logger.Debug("[BrowserExtract] " + req.Method + " " + Truncate(req.Url, 180) +
                        "  resource=" + req.ResourceType);
                    requestsLogged++;
                }
                var match = MatchMedia(req.Url);
                if (match != null && (winner == null || match.Value.Priority < winner.Value.Priority))
                {
                    winner = (req.Url, match.Value.Priority, new Dictionary<string, string>(hdrs));
                    _logger.Info("[BrowserExtract] Media URL candidate (" + match.Value.Label + "): " + Truncate(req.Url, 180));
                    // Don't cancel immediately — allow a brief window to catch a higher-priority
                    // match (e.g. m3u8 arriving after an mp4 probe). We'll cut off in the wait loop.
                }
            };

            // Kick off navigation but don't await the full load — many video pages never reach
            // 'load' state because they stream indefinitely. A ~NetworkIdle2 wait or manual poll is better.
            _logger.Info("[BrowserExtract] Navigating to " + Truncate(pageUrl, 200) + " (deadline " + deadline.TotalSeconds + "s)");
            var navigationTask = page.GoToAsync(pageUrl, new NavigationOptions
            {
                Timeout = (int)deadline.TotalMilliseconds,
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded }
            });

            var winnerWaitTask = WaitForWinnerAsync(() => winner, deadline, linkedCts.Token);

            // First to complete: either the page errored/loaded, or we captured a media URL.
            var completed = await Task.WhenAny(navigationTask, winnerWaitTask);

            // Let a small tail window pass so m3u8 can arrive after an initial mp4 probe.
            if (winner != null)
            {
                await Task.Delay(400, CancellationToken.None).ContinueWith(_ => { });
            }

            if (winner == null)
            {
                _logger.Warning("[BrowserExtract] No media URL captured within " + deadline.TotalSeconds + "s (" + requestsLogged + " requests seen).");
                return null;
            }

            // Harvest cookies for the resolved URL's origin.
            var cookieHeader = await BuildCookieHeaderAsync(page, winner.Value.Url);

            // Probe-first: does the origin accept the URL with an AVPro UA and no special cookies?
            bool needsSession = await ProbeNeedsSessionAsync(winner.Value.Url, ct);

            if (needsSession)
            {
                string host = BrowserSessionCache.HostFromUrl(winner.Value.Url);
                var session = new BrowserSession(
                    Host: host,
                    ResolvedUrl: winner.Value.Url,
                    Headers: SanitizeHeadersForReplay(winner.Value.Headers),
                    CookieHeader: cookieHeader,
                    CapturedAt: DateTime.UtcNow,
                    Expires: DateTime.UtcNow.Add(BrowserSessionCache.DefaultTtl)
                );
                _sessionCache.Put(session);
                _logger.Info("[BrowserExtract] Probe returned gated — session cached for " + host +
                    " (headers=" + session.Headers.Count + ", cookie-len=" + cookieHeader.Length + ").");
            }
            else
            {
                _logger.Info("[BrowserExtract] Probe succeeded without cookies — returning pristine URL.");
            }

            sw.Stop();
            return new BrowserExtractResult(
                MediaUrl: winner.Value.Url,
                Height: null,
                SessionCached: needsSession,
                RequestsLogged: requestsLogged,
                ElapsedMs: sw.ElapsedMilliseconds
            );
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("[BrowserExtract] Cancelled after " + sw.ElapsedMilliseconds + "ms.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning("[BrowserExtract] Extraction failed: " + ex.GetType().Name + " — " + ex.Message);
            return null;
        }
        finally
        {
            if (page != null)
            {
                try { await page.CloseAsync(); }
                catch (Exception ex) { _logger.Debug("[BrowserExtract] Page close error: " + ex.Message); }
            }
        }
    }

    // Polls for a non-null winner until deadline or cancel.
    private static async Task WaitForWinnerAsync(Func<(string Url, int Priority, IDictionary<string, string> Headers)?> poll, TimeSpan deadline, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < deadline && !ct.IsCancellationRequested)
        {
            if (poll() != null) return;
            await Task.Delay(100, ct).ContinueWith(_ => { });
        }
    }

    private static (string Label, int Priority)? MatchMedia(string url)
    {
        if (NonMediaPathExclusion.IsMatch(url)) return null;
        foreach (var (label, pat, prio) in MediaPatterns)
            if (pat.IsMatch(url)) return (label, prio);
        return null;
    }

    private async Task<string> BuildCookieHeaderAsync(IPage page, string url)
    {
        try
        {
            var cookies = await page.GetCookiesAsync(url);
            if (cookies == null || cookies.Length == 0) return "";
            var sb = new StringBuilder();
            foreach (var c in cookies)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(c.Name).Append('=').Append(c.Value);
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.Debug("[BrowserExtract] Cookie harvest failed: " + ex.Message);
            return "";
        }
    }

    // HEAD probe with an AVPro-style UA. Returns true if the origin requires the captured session
    // (401/403/429), false if it accepts AVPro's default request (200/2xx/3xx). Treats timeout /
    // network errors as "needs session" — safer to wrap than to ship a URL AVPro will then fail on.
    private async Task<bool> ProbeNeedsSessionAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _probeClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)resp.StatusCode;
            // HEAD may be blocked (405) by some CDNs — fall back to a Range: bytes=0-0 GET.
            if (code == 405 || code == 501)
            {
                using var getReq = new HttpRequestMessage(HttpMethod.Get, url);
                getReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var getResp = await _probeClient.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, ct);
                code = (int)getResp.StatusCode;
            }
            return code == 401 || code == 403 || code == 429;
        }
        catch
        {
            // Probe failed. Default to "needs session" so we carry the captured auth forward — the
            // alternative (ship pristine URL, hope AVPro succeeds) fails louder when the site is gated.
            return true;
        }
    }

    // Drop hop-by-hop and pseudo headers that Puppeteer adds; keep the real fingerprint.
    private static readonly HashSet<string> StripHeaders = new(StringComparer.OrdinalIgnoreCase) {
        "host", "connection", "content-length", ":method", ":path", ":scheme", ":authority", "cookie"
    };

    private static IReadOnlyDictionary<string, string> SanitizeHeadersForReplay(IDictionary<string, string> raw)
    {
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in raw)
        {
            if (string.IsNullOrEmpty(kvp.Key)) continue;
            if (StripHeaders.Contains(kvp.Key)) continue;
            if (kvp.Key.StartsWith(":")) continue;
            clean[kvp.Key] = kvp.Value;
        }
        return clean;
    }

    private async Task<IBrowser?> GetOrInitBrowserAsync(CancellationToken ct)
    {
        if (_browser != null && !_browser.IsClosed) return _browser;
        await _browserInitLock.WaitAsync(ct);
        try
        {
            if (_browser != null && !_browser.IsClosed) return _browser;

            string? exe = _resolvedExecutablePath ?? ResolveBrowserExecutable();
            if (exe == null)
            {
                _logger.Warning("[BrowserExtract] No Chromium/Edge/Chrome found on this system. " +
                    "Set Config.DownloadBundledChromium=true to auto-download ~180 MB Chromium.");
                if (_settings.Config.DownloadBundledChromium)
                {
                    exe = await DownloadBundledChromiumAsync(ct);
                }
                if (exe == null) return null;
            }
            _resolvedExecutablePath = exe;

            _logger.Info("[BrowserExtract] Launching headless browser: " + exe);
            var options = new LaunchOptions
            {
                Headless = true,
                ExecutablePath = exe,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--mute-audio",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--disable-extensions",
                    "--disable-dev-shm-usage",
                    "--disable-gpu"
                }
            };
            _browser = await Puppeteer.LaunchAsync(options);
            _browser.Closed += (_, _) => _logger.Debug("[BrowserExtract] Browser process exited.");
            return _browser;
        }
        catch (Exception ex)
        {
            // PuppeteerSharp wraps the real cause (wrong browser version, missing DLLs, exit-code
            // from the child, etc.) inside InnerException — the outer message is usually just
            // "Failed to launch browser!". Walk the whole chain so the operator can actually act.
            _logger.Warning("[BrowserExtract] Browser launch failed: " + FormatExceptionChain(ex));
            return null;
        }
        finally { _browserInitLock.Release(); }
    }

    private static string FormatExceptionChain(Exception ex)
    {
        var sb = new StringBuilder();
        Exception? cur = ex;
        while (cur != null)
        {
            if (sb.Length > 0) sb.Append(" | inner: ");
            sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
            cur = cur.InnerException;
        }
        return sb.ToString();
    }

    // Chromium-family detection. Edge ships on every Win11 box, so we prefer it. We do NOT fall
    // through to bundled Chromium automatically — that's a 180 MB download on first use and should
    // only happen when the user opts in via config.
    private static string? ResolveBrowserExecutable()
    {
        string[] candidates =
        {
            // Edge (preferred — pre-installed on Windows 10/11)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Microsoft", "Edge", "Application", "msedge.exe"),
            // Chrome
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            // Brave (Chromium-based; ships on some systems pre-configured)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
        };
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
        return null;
    }

    private async Task<string?> DownloadBundledChromiumAsync(CancellationToken ct)
    {
        try
        {
            _logger.Info("[BrowserExtract] Downloading bundled Chromium (~180 MB, one-time)...");
            var fetcher = new BrowserFetcher();
            var info = await fetcher.DownloadAsync();
            _logger.Info("[BrowserExtract] Bundled Chromium downloaded: " + info.GetExecutablePath());
            return info.GetExecutablePath();
        }
        catch (Exception ex)
        {
            _logger.Warning("[BrowserExtract] Chromium download failed: " + ex.Message);
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 3) + "...";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _browser?.CloseAsync().GetAwaiter().GetResult(); } catch { }
        try { _probeClient.Dispose(); } catch { }
    }
}
