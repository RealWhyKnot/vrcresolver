using System;
using System.Net;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.IPC;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;

public record YtDlpResult(string Url, int? Height, int? Width, string? Vcodec, string? FormatId, string? Protocol);

// ResolutionEngine is split into a few partial files by concern:
//   - ResolutionEngine.cs               â€” fields, ctor, top-level Resolve* entry points, the main
//                                         cascade loop, playback-feedback wiring, relay-wrap, URL
//                                         classification, recent-resolutions ring.
//   - ResolutionEngine.Tiers.cs         â€” actual tier attempt methods (AttemptTier1/2/3,
//                                         ResolveTier1/2/3, RunTier1Attempt, RunBrowserExtract,
//                                         ResolveStreamlink, plus the cold-race builder).
//   - ResolutionEngine.YtDlpProcess.cs  â€” yt-dlp / yt-dlp-og subprocess invocation, stdout/stderr
//                                         parsing, bgutil-plugin argv assembly, probe-header
//                                         dictionaries, bot-detection regex.
//
// All three compile into the same `partial class ResolutionEngine` â€” runtime IL is identical to
// the pre-split monolith. Splits are mechanical: move method, no behavior change.
[SupportedOSPlatform("windows")]
public partial class ResolutionEngine
{
    private readonly Logger _logger;
    private readonly SettingsManager _settings;
    private readonly VrcLogMonitor _monitor;
    private readonly HttpClient _httpClient;
    private readonly Tier2WebSocketClient _tier2Client;
    private readonly HostsManager _hostsManager;
    private readonly RelayPortManager _relayPortManager;
    private readonly PatcherService _patcher;
    private readonly CurlImpersonateClient? _curlClient;
    private readonly PotProviderService? _potProvider;
    private readonly BrowserExtractService? _browserExtractor;
    private readonly WarpService? _warp;
    private readonly ReportingService? _reporting;
    private SystemEventBus? _eventBus;

    public event Action<string, object>? OnStatusUpdate;
    private int _activeResolutions = 0;
    private readonly ConcurrentDictionary<string, int> _tierCounts = new(
        new[] {
            new KeyValuePair<string, int>("tier0", 0),
            new KeyValuePair<string, int>("tier1", 0),
            new KeyValuePair<string, int>("tier2", 0),
            new KeyValuePair<string, int>("tier3", 0),
            new KeyValuePair<string, int>("tier4", 0)
        });

    // Per-host/per-strategy learning. See StrategyMemory.cs for semantics (success/failure decay,
    // demotion, migration from the old tier_memory.json).
    private readonly StrategyMemory _strategyMemory;
    public StrategyMemory StrategyMemory => _strategyMemory;

    // Well-known UA string that matches VRChat's Unity build so pre-flight probes (Tier 1 / Tier 3)
    // pass allowlists on "VRChat movie world" hosts (vr-m.net etc.) that reject anonymous clients.
    // Seed value only â€” Phase 3 will capture the actual UA from incoming AVPro relay requests.
    internal const string VrchatAvProUserAgent = "UnityPlayer/2022.3.22f1 (UnityWebRequest/1.0, libcurl/7.84.0-DEV)";
    internal const string VrchatReferer = "https://vrchat.com/";

    // Domain-level "requires PO token" flag. YouTube's bot-detection mode is not per-video â€” once it
    // flips on, the whole domain requires PO tokens for a window of time (~30 min in practice). We
    // flag the host on the first bot-check stderr, then every Tier 1 call to that host pays the PO
    // token cost upfront. When the flag expires, we try the fast-path (no PO) again on the next call.
    // Value = absolute expiry timestamp (UTC).
    private readonly ConcurrentDictionary<string, DateTime> _domainRequiresPot = new();
    private static readonly TimeSpan DomainRequiresPotTtl = TimeSpan.FromMinutes(30);

    // Per-host cache of Streamlink's "--can-handle-url" answer. Keyed by host (lowercase). Negative
    // answers expire after 24h (Streamlink plugin list rarely grows mid-session); positive answers
    // expire after 7d. Saves ~500ms per request for hosts Streamlink doesn't support (e.g. vr-m.net).
    private readonly ConcurrentDictionary<string, (bool CanHandle, DateTime Expiry)> _streamlinkCapabilityCache = new();
    private static readonly TimeSpan StreamlinkCacheTtlNegative = TimeSpan.FromHours(24);
    private static readonly TimeSpan StreamlinkCacheTtlPositive = TimeSpan.FromDays(7);

    // Positive resolve cache: short-TTL (URL, player) â†’ resolved URL. VRChat calls yt-dlp multiple times
    // for the same video (thumbnail probe + duration probe + actual play). Without this, each call hits
    // whyknot.dev fresh, burning ~6s per trip. Cache TTL stays short to keep CDN URLs (which expire
    // server-side) fresh and lets transient failures self-heal on the next real play.
    //
    // MaxHits caps replays: 2-3 calls per play-event is the normal VRChat pattern, but AVPro retrying
    // a broken URL shows up as 4+ calls within the TTL. After MaxHits, the entry self-invalidates so
    // the next attempt re-resolves â€” previously we'd keep serving a stale URL for the full 90s window
    // even when AVPro was obviously bouncing off it (e.g. SoundCloud signed-URL expiry).
    private record ResolveCacheEntry(YtDlpResult Result, string Tier, DateTime Expires, int Hits);
    private readonly ConcurrentDictionary<string, ResolveCacheEntry> _resolveCache = new();
    private static readonly TimeSpan ResolveCacheTtl = TimeSpan.FromSeconds(90);
    private const int ResolveCacheMaxHits = 3;
    public static string ResolveCacheKey(string url, string player) => player + "|" + url;

    // Per-host rolling-window budget for tier-1 (yt-dlp) spawns. Prevents the cold race from
    // machine-gunning a single host (e.g. YouTube) when the user plays videos in quick succession
    // or iterates on builds. When budget is exhausted for a host, tier-1 strategies are skipped
    // and the race falls through to tier-2 cloud â€” which egresses from a different IP and doesn't

    public ResolutionEngine(Logger logger, SettingsManager settings, VrcLogMonitor monitor, Tier2WebSocketClient tier2Client, HostsManager hostsManager, RelayPortManager relayPortManager, PatcherService patcher, CurlImpersonateClient? curlClient = null, PotProviderService? potProvider = null, BrowserExtractService? browserExtractor = null, WarpService? warp = null, ReportingService? reporting = null)
    {
        _logger = logger;
        _settings = settings;
        _monitor = monitor;
        _tier2Client = tier2Client;
        _hostsManager = hostsManager;
        _relayPortManager = relayPortManager;
        _patcher = patcher;
        _curlClient = curlClient;
        _potProvider = potProvider;
        _browserExtractor = browserExtractor;
        _warp = warp;
        _reporting = reporting;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_settings.Config.UserAgent);

        // Initialize counts from history
        foreach (var entry in _settings.Config.History)
        {
            var tierKey = entry.Tier.Split('-')[0];
            _tierCounts.AddOrUpdate(tierKey, 1, (_, v) => v + 1);
        }

        _strategyMemory = new StrategyMemory(_logger, AppDomain.CurrentDomain.BaseDirectory);
        _strategyMemory.Load();

        // Close the feedback loop: when AVPro reports Loading failed, find the strategy that
        // produced that URL and demote it, so the next request doesn't re-use the broken
        // fast-path. Playback failure â†’ PlaybackFailed â†’ one-strike demote (see StrategyMemory).
        _monitor.OnAvProLoadFailure += HandleAvProLoadFailure;

        LogActiveTiers();
    }

    private void LogActiveTiers()
    {
        var all = new[] {
            ("tier0", "streamlink"),
            ("tier1", "yt-dlp"),
            ("tier2", "cloud"),
            ("tier3", "yt-dlp-og"),
            ("tier4", "passthrough"),
        };
        var disabledRaw = _settings.Config.DisabledTiers ?? new List<string>();
        bool tier4WasDisabled = disabledRaw.Any(t => string.Equals(t, "tier4", StringComparison.OrdinalIgnoreCase));
        if (tier4WasDisabled)
            _logger.Warning("Config requested tier4 (passthrough) be disabled, but tier4 is the always-return-something backstop and cannot be turned off. Ignoring.");
        var disabled = disabledRaw.Where(t => !string.Equals(t, "tier4", StringComparison.OrdinalIgnoreCase)).ToList();
        var active = all.Where(t => !disabled.Contains(t.Item1)).Select(t => t.Item2);
        var off = all.Where(t => disabled.Contains(t.Item1)).Select(t => t.Item2).ToList();
        string line = "Active tiers: " + string.Join(", ", active);
        if (off.Count > 0) line += " (disabled: " + string.Join(", ", off) + ")";
        _logger.Info(line);
    }

    public void SetEventBus(SystemEventBus bus) { _eventBus = bus; }

    private void UpdateStatus(string message, RequestContext? ctx = null)
    {
        var stats = new {
            activeCount = _activeResolutions,
            tierStats = _tierCounts,
            node = _tier2Client.ActiveNode,
            player = _monitor.CurrentPlayer,
            correlationId = ctx?.CorrelationId
        };
        OnStatusUpdate?.Invoke(message, stats);
        _eventBus?.PublishStatus("ResolutionEngine", message, stats, ctx?.CorrelationId);
    }

    // BuildBinaryProbeHeaders / BuildHlsProbeHeaders moved to ResolutionEngine.YtDlpProcess.cs.

    // Verify a resolved URL is reachable before accepting it. Designed to be indistinguishable
    // from AVPro's own first fetch so CDNs that 403 probes won't fingerprint us.
    // - Binary streams (MP4, DASH, proxy URLs): Range: bytes=0-4095 â€” same as a real initial
    //   segment fetch, not the tell-tale bytes=0-0 pattern scanners use.
    // - HLS manifests: plain GET with manifest Accept header.
    // Prefers curl-impersonate (Chrome TLS fingerprint). Falls back to HttpClient with matching
    // headers when curl-impersonate isn't available.
    private async Task<bool> CheckUrlReachable(string url, RequestContext ctx)
    {
        string shortUrl = url.Length > 100 ? url.Substring(0, 100) + "..." : url;
        bool isHls = url.Contains(".m3u8") || url.Contains("m3u8");
        var headers = isHls ? BuildHlsProbeHeaders() : BuildBinaryProbeHeaders();
        string probeMode = isHls ? "HLS manifest GET" : "initial-segment GET (Range 0-4095)";

        if (_curlClient?.IsAvailable == true)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] Reachability check via curl-impersonate [" + probeMode + "]: " + shortUrl);
            int status = await _curlClient.CheckReachabilityAsync(url, headers);
            if (status == -1)
            {
                // Probe timed out or process error â€” cannot confirm reachability, but do not reject.
                // Streaming servers (e.g. private HLS, proxy URLs) often do not respond to probe
                // requests within 5s. Rejecting on timeout causes valid URLs to cascade needlessly.
                _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: curl-impersonate timed out for " + shortUrl + " â€” accepting URL (benefit of the doubt).");
                return true;
            }
            bool reachable = status is (>= 200 and < 400) or 416;
            if (!reachable)
                _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: curl-impersonate returned HTTP " + status + " [" + probeMode + "] for " + shortUrl);
            else
                _logger.Debug("[" + ctx.CorrelationId + "] Reachability check: curl-impersonate HTTP " + status + " â€” OK for " + shortUrl);
            return reachable;
        }

        // Fallback: plain HttpClient with AVPro-shaped headers. Same request shape curl-impersonate
        // would send, minus the Chrome TLS fingerprint. Some CDNs will still 403 the plain .NET
        // handshake â€” we accept on timeout to avoid false-negative cascading.
        _logger.Debug("[" + ctx.CorrelationId + "] Reachability check via HttpClient [" + probeMode + "] (curl-impersonate unavailable): " + shortUrl);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!isHls) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4095);
            req.Headers.Remove("User-Agent");
            foreach (var kv in headers)
            {
                if (string.Equals(kv.Key, "Range", StringComparison.OrdinalIgnoreCase)) continue;
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            int status = (int)resp.StatusCode;
            bool reachable = status < 400 || status == 416;
            if (!reachable)
                _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: HttpClient returned HTTP " + status + " [" + probeMode + "] for " + shortUrl);
            else
                _logger.Debug("[" + ctx.CorrelationId + "] Reachability check: HttpClient HTTP " + status + " â€” OK for " + shortUrl);
            return reachable;
        }
        catch (OperationCanceledException)
        {
            // Probe timed out â€” accept with warning rather than rejecting a potentially valid URL.
            _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: HttpClient timed out for " + shortUrl + " â€” accepting URL (benefit of the doubt).");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning("[" + ctx.CorrelationId + "] Reachability check error (" + ex.GetType().Name + ") [" + probeMode + "] for " + shortUrl + " â€” " + ex.Message);
            return false;
        }
    }

    private void RecordStrategySuccess(string memKey, string strategyName, int? resolvedHeight = null)
    {
        if (string.IsNullOrEmpty(memKey) || !_settings.Config.EnableTierMemory) return;
        _strategyMemory.RecordSuccess(memKey, strategyName, resolvedHeight);
    }

    private void RecordStrategyFailure(string memKey, string strategyName, StrategyFailureKind kind = StrategyFailureKind.Unknown)
    {
        if (string.IsNullOrEmpty(memKey) || !_settings.Config.EnableTierMemory) return;
        _strategyMemory.RecordFailure(memKey, strategyName, kind);
    }

    // True if the host is currently in "needs PO token" mode due to a recent bot-detection failure.
    // Self-cleaning: expired entries are removed on lookup.
    private bool DomainRequiresPot(string host)
    {
        if (!_domainRequiresPot.TryGetValue(host, out var expires)) return false;
        if (DateTime.UtcNow >= expires)
        {
            _domainRequiresPot.TryRemove(host, out _);
            return false;
        }
        return true;
    }

    // Flag a host as requiring PO tokens for the next TTL window. Bounded cleanup runs inline.
    private void MarkDomainRequiresPot(string host)
    {
        _domainRequiresPot[host] = DateTime.UtcNow.Add(DomainRequiresPotTtl);
        if (_domainRequiresPot.Count > 32)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _domainRequiresPot)
                if (kv.Value <= now) _domainRequiresPot.TryRemove(kv.Key, out _);
        }
    }

    // Normalize a URL's host for domain-key lookup ("www.youtube.com" â†’ "youtube.com", "youtu.be" stays).
    // Returns the empty string if the URL is malformed.
    // Runs a tier resolver and measures how long it takes.
    private static async Task<(T Result, long ElapsedMs)> TimedResolve<T>(Func<Task<T>> resolver)
    {
        var sw = Stopwatch.StartNew();
        T result = await resolver();
        sw.Stop();
        return (result, sw.ElapsedMilliseconds);
    }

    // Map PreferredResolution ("1080p") to an integer height; defaults to 1080 when unparseable.
    private int ParsePreferredHeight()
    {
        string res = _settings.Config.PreferredResolution?.Replace("p", "") ?? "1080";
        return int.TryParse(res, out var h) ? h : 1080;
    }

    // Quality floor: accept a resolved stream whose height is at least 2/3 of preferred.
    // Pref=1080p â†’ floor=720p âœ“, pref=720p â†’ floor=480p âœ“, pref=480p â†’ floor=320p.
    public static int ComputeQualityFloor(int preferredHeight) =>
        (int)(preferredHeight * 2.0 / 3.0);

    // A tier result is "good enough" if we don't know its height (trust-by-default) or it's â‰¥ floor.
    public static bool IsAcceptableQuality(int? resolvedHeight, int floorHeight) =>
        resolvedHeight == null || resolvedHeight >= floorHeight;

    // === WHY WE WRAP ===
    // VRChat enforces a trusted-URL list inside AVPro. Media URLs whose host does not match the
    // allowlist (e.g. *.youtube.com, youtu.be, vimeo.com, â€¦) are silently rejected with
    // "[AVProVideo] Error: Loading failed. File not found, codec not supported, video resolution
    // too high or insufficient system resources." The relay wrap rewrites any URL as
    //   http://localhost.youtube.com:{port}/play?target=<base64>
    // which â€” via the hosts-file mapping `127.0.0.1 localhost.youtube.com` â€” routes to our local
    // relay while AVPro sees a trusted *.youtube.com host. This is the ONLY reason untrusted
    // cloud/proxy URLs (node1.whyknot.dev from tier2:cloud-whyknot, signed-URL CDNs, etc.) play at
    // all. Wrapping is the DEFAULT; do not "optimize" by skipping it for non-YouTube hosts â€” every
    // untrusted URL that reaches AVPro pristine will silently fail playback.
    //
    // The single narrow exception is the config-driven deny-list `AppConfig.NativeAvProUaHosts`,
    // which holds hosts that serve only to AVPro's UnityPlayer UA (VRChat "movie worlds" like
    // vr-m.net). Wrapping those breaks UA passthrough and the origin 403s us. Every addition to
    // that list must be backed by a log capture showing the host working pristine but failing
    // through the relay. See the feedback_relay_purpose memory for history.

    public Task<string?> ResolveAsync(ResolvePayload payload) =>
        ResolveInternalAsync(payload, skipRelayWrap: false, playerOverride: null, historyPlayerLabel: null);

    // Resolve a URL for the Share panel: same cascade, but never wrap in localhost.youtube.com relay URL
    // (that URL only works inside the user's own VRChat). History entries tagged with `historyPlayerLabel`
    // ("CloudShare" / "P2PShare") so they show up in history but are distinguishable.
    public Task<string?> ResolveForShareAsync(string url, string player, string shareMode)
    {
        var payload = new ResolvePayload { Args = new[] { url, player == "AVPro" ? "AVProVideo" : "UnityPlayer" } };
        return ResolveInternalAsync(payload, skipRelayWrap: true, playerOverride: player, historyPlayerLabel: shareMode);
    }

    private async Task<string?> ResolveInternalAsync(ResolvePayload payload, bool skipRelayWrap, string? playerOverride, string? historyPlayerLabel)
    {
        Interlocked.Increment(ref _activeResolutions);
        var resolutionSw = Stopwatch.StartNew();

        string? targetUrl = payload.Args.FirstOrDefault(a => a.StartsWith("http"));
        if (string.IsNullOrEmpty(targetUrl))
        {
            Interlocked.Decrement(ref _activeResolutions);
            _logger.Warning("No valid URL found in resolution payload.");
            return null;
        }

        var ctx = RequestContext.Create(targetUrl);

        string player = playerOverride ?? _monitor.CurrentPlayer;
        if (playerOverride == null)
        {
            if (payload.Args.Any(a => a.Contains("AVProVideo"))) player = "AVPro";
            if (payload.Args.Any(a => a.Contains("UnityPlayer"))) player = "Unity";
        }

        _logger.Info("[" + ctx.CorrelationId + "] Starting resolution for: " + targetUrl + " [" + player + "]" + (skipRelayWrap ? " [share]" : ""));
        UpdateStatus("Intercepted " + player + " request...", ctx);

        // Positive resolve cache: collapse redundant calls from VRChat (which spawns yt-dlp 2-3x per
        // play event for thumbnail/duration/actual-play). A cache hit bypasses the full cascade and
        // returns the already-resolved URL, just re-applying the relay wrap (port may have changed).
        // History/stat writes are skipped on hit so the UI doesn't show duplicate rows.
        string cacheKey = ResolveCacheKey(targetUrl, player);
        if (_resolveCache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow < cached.Expires && cached.Hits < ResolveCacheMaxHits)
            {
                _resolveCache[cacheKey] = cached with { Hits = cached.Hits + 1 };
                string cachedFinal = ApplyRelayWrap(cached.Result.Url, skipRelayWrap, ctx.CorrelationId);
                resolutionSw.Stop();
                Interlocked.Decrement(ref _activeResolutions);
                UpdateStatus("Cached resolution via " + cached.Tier.ToUpper(), ctx);
                string shortCached = cachedFinal.Length > 100 ? cachedFinal.Substring(0, 100) + "..." : cachedFinal;
                _logger.Success("[" + ctx.CorrelationId + "] Final Resolution [" + cached.Tier + "] [cache-hit " + (cached.Hits + 1) + "/" + ResolveCacheMaxHits + "] in " + resolutionSw.ElapsedMilliseconds + "ms: " + shortCached);
                return cachedFinal;
            }
            _resolveCache.TryRemove(cacheKey, out _);
        }

        YtDlpResult? winnerResult = null;
        YtDlpResult? bestSoFar = null;
        string bestSoFarTier = "";
        // Set by the browser-extract strategy. Tells ApplyRelayWrap to wrap even non-YouTube URLs
        // so the relay can replay the captured browser session (cookies + headers) to AVPro.
        bool winnerForcesRelayWrap = false;
        string activeTier = _settings.Config.PreferredTier;
        // Clamp: tier4 is the "always return something" backstop. It CANNOT be disabled â€” the
        // program must never reach a state where resolution returns null and VRChat gets nothing.
        // If config lists tier4 as disabled, strip it silently here (warned at startup in
        // LogActiveTiers).
        var disabled = (_settings.Config.DisabledTiers ?? new List<string>())
            .Where(t => !string.Equals(t, "tier4", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int preferredH = ParsePreferredHeight();
        int floorH = ComputeQualityFloor(preferredH);

        // Captured across the try/catch so the recent-resolutions recording after the block can
        // tie the final URL back to the strategy + host that produced it.
        string finalMemKey = "";
        string finalStrategy = "";

        try
        {
            if (activeTier == "tier4")
            {
                _logger.Info("[" + ctx.CorrelationId + "] Tier 4 active: Returning original URL (Passthrough)");
                winnerResult = new YtDlpResult(targetUrl, null, null, null, null, null);
            }
            else if (RequiresNativeAvProUa(targetUrl))
            {
                // The deny-list (NativeAvProUaHosts) marks hosts whose origin only serves to AVPro's
                // native UA; wrapping breaks playback and yt-dlp on these hosts has no path to a
                // better URL (the manifest is already final). Without this branch, the cascade still
                // runs through tier1â†’2â†’3, all fail, and the user waits 30+ seconds for an inevitable
                // tier4 fallback (see vr-m.net 32-second resolution at 13:54:45 in the 04-26 logs).
                string host = Uri.TryCreate(targetUrl, UriKind.Absolute, out var u) ? u.Host : "<unparseable>";
                _logger.Info("[" + ctx.CorrelationId + "] " + host + " is in NativeAvProUaHosts â€” short-circuiting to tier 4 passthrough (cascade would only waste time).");
                winnerResult = new YtDlpResult(targetUrl, null, null, null, null, null);
                activeTier = "tier4";
            }
            else
            {
                // Build ordered cascade starting from preferred tier, skipping disabled tiers
                var allTiers = new[] { "tier1", "tier2", "tier3" };
                int startIdx = Array.IndexOf(allTiers, activeTier);
                if (startIdx < 0)
                {
                    _logger.Warning("[" + ctx.CorrelationId + "] Unknown preferred tier '" + activeTier + "', defaulting to tier1.");
                    startIdx = 0;
                }
                var cascade = allTiers.Skip(startIdx).Where(t => !disabled.Contains(t)).ToList();

                bool isStreamlinkLive = !disabled.Contains("tier0") && await StreamlinkCanHandleUrlAsync(targetUrl, ctx);
                // Live classification for memory keying: streamlink-handles is a *capability*
                // signal, not a liveness signal. URL-pattern check catches /live/ paths even
                // when streamlink isn't installed or doesn't claim the host â€” without it, a
                // YouTube /live/ URL inherits the VOD fast-path memory and hangs on tier2.
                bool isLiveForMemory = isStreamlinkLive || StrategyMemory.LooksLikeLive(targetUrl);
                // Include the player in the memory key â€” AVPro and Unity need different formats,
                // so what wins for one loses for the other. Sharing memory across them poisoned
                // Unity's fast-path with AVPro-validated strategies whose output Unity can't play.
                string memKey = StrategyMemory.KeyFor(targetUrl, isLiveForMemory, player);
                finalMemKey = memKey;

                StrategyMemoryEntry? remembered = null;
                string? rememberedGroup = null;
                if (_settings.Config.EnableTierMemory && !string.IsNullOrEmpty(memKey))
                {
                    remembered = _strategyMemory.GetPreferred(memKey);
                    if (remembered != null)
                    {
                        rememberedGroup = remembered.StrategyName.Split(':')[0];
                        _logger.Debug("[" + ctx.CorrelationId + "] [StrategyMemory] Preferred '" + remembered.StrategyName + "' for " + memKey + " (" + remembered.SuccessCount + "W/" + remembered.FailureCount + "L).");
                    }
                }

                _logger.Info("[" + ctx.CorrelationId + "] Cascade: " + string.Join(" â†’ ", cascade.Select(t => t.ToUpper())) +
                    (disabled.Count > 0 ? " (disabled: " + string.Join(", ", disabled) + ")" : "") +
                    " (quality floor " + floorH + "p)");

                // activeStrategy is the specific variant label written to StrategyMemory on success.
                // activeTier is the tier-group (backwards compat for UI / HistoryEntry.Tier).
                string activeStrategy = "";

                // Tier 0: Streamlink â€” live-stream fast-path. Skipped when a faster (tier1/2/3)
                // winner is already remembered. Streamlink does not report resolution, so the
                // quality heuristic treats it as unknown â†’ accepted.
                if (isStreamlinkLive && !disabled.Contains("tier0"))
                {
                    bool tryStreamlink = rememberedGroup == null || rememberedGroup == "tier0";
                    if (tryStreamlink)
                    {
                        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Streamlink supports this URL â€” attempting resolution.");
                        var (slRes, slMs) = await TimedResolve(() => ResolveStreamlink(targetUrl, ctx));
                        if (slRes != null)
                        {
                            _logger.Info("[" + ctx.CorrelationId + "] [Tier 0] Streamlink success in " + slMs + "ms.");
                            winnerResult = slRes; activeTier = "tier0-streamlink"; activeStrategy = "tier0:streamlink-native";
                        }
                        else if (rememberedGroup == "tier0")
                        {
                            _logger.Warning("[" + ctx.CorrelationId + "] [StrategyMemory] Remembered tier0 strategy failed â€” demoting.");
                            RecordStrategyFailure(memKey, remembered!.StrategyName);
                            remembered = null; rememberedGroup = null;
                        }
                        else
                        {
                            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Streamlink returned no URL after " + slMs + "ms â€” cascading.");
                        }
                    }
                }

                if (winnerResult == null)
                {
                    int cascadeStart = 0;
                    if (rememberedGroup != null && rememberedGroup != "tier0")
                    {
                        int ri = cascade.IndexOf(rememberedGroup);
                        if (ri > 0)
                        {
                            cascadeStart = ri;
                            _logger.Debug("[" + ctx.CorrelationId + "] [StrategyMemory] Jumping to remembered group '" + rememberedGroup + "' (skipping " + ri + " earlier tier(s)).");
                        }
                    }

                    // Specific-strategy fast-path. Memory tracks strategy NAMES (e.g.
                    // "tier1:browser-extract"), but the sequential cascade below only knows tier GROUPS
                    // â€” it would run the tier's default recipe (ResolveTier1 etc.), which is a
                    // completely different yt-dlp arg set from the remembered variant. That bug
                    // previously caused a remembered "tier1:browser-extract" winner on YouTube to run
                    // as "tier1:default", get bot-checked, and fall through to Tier 2 cloud. Look up
                    // the exact strategy in the catalog and run it first with a soft deadline. On
                    // failure, demote the specific entry and fall through to the cold race so the
                    // dispatcher can rediscover a working variant.
                    if (remembered != null && rememberedGroup != null && rememberedGroup != "tier0"
                        && !disabled.Contains(rememberedGroup))
                    {
                        var catalogForFast = BuildColdRaceStrategies(targetUrl, player, payload.Args, disabled);
                        var specific = catalogForFast.FirstOrDefault(s =>
                            string.Equals(s.Name, remembered.StrategyName, StringComparison.OrdinalIgnoreCase));
                        if (specific != null)
                        {
                            _logger.Info("[" + ctx.CorrelationId + "] [StrategyMemory] Fast-path: running remembered '"
                                + specific.Name + "' (" + remembered.SuccessCount + "W/" + remembered.FailureCount + "L).");
                            using var fastCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                            var fastSctx = new StrategyRunContext(targetUrl, player, payload.Args, ctx, floorH, fastCts.Token);
                            var fastSw = Stopwatch.StartNew();
                            YtDlpResult? fastRes = null;
                            try { fastRes = await specific.Executor(fastSctx); }
                            catch (Exception ex) { _logger.Debug("[" + ctx.CorrelationId + "] Fast-path '" + specific.Name + "' threw: " + ex.Message); }
                            fastSw.Stop();
                            // Pre-handoff probe: even a "remembered winner" can return a now-dead
                            // URL (host rotated, token expired, CDN rejected). Verify before
                            // committing. On probe failure, demote with PlaybackFailed and cold-race.
                            bool fastProbed = true;
                            if (fastRes != null && _settings.Config.EnablePreflightProbe && !specific.Group.StartsWith("tier1"))
                            {
                                fastProbed = await CheckUrlReachable(fastRes.Url, ctx);
                                if (!fastProbed)
                                    _logger.Warning("[" + ctx.CorrelationId + "] [StrategyMemory] Fast-path '" + specific.Name + "' URL failed pre-flight probe.");
                            }
                            if (fastRes != null && fastProbed && IsAcceptableQuality(fastRes.Height, floorH))
                            {
                                winnerResult = fastRes; activeTier = specific.Group; activeStrategy = specific.Name;
                                winnerForcesRelayWrap = specific.ForceRelayWrap;
                                _logger.Info("[" + ctx.CorrelationId + "] [StrategyMemory] Fast-path '" + specific.Name
                                    + "' won in " + fastSw.ElapsedMilliseconds + "ms"
                                    + (fastRes.Height is int fh ? " at " + fh + "p." : "."));
                            }
                            else
                            {
                                string reason = fastCts.IsCancellationRequested ? "timed out"
                                    : (fastRes == null ? "no result"
                                       : !fastProbed ? "probe rejected"
                                       : "below floor (" + fastRes.Height + "p)");
                                _logger.Warning("[" + ctx.CorrelationId + "] [StrategyMemory] Fast-path '" + specific.Name
                                    + "' failed (" + reason + ") in " + fastSw.ElapsedMilliseconds + "ms â€” demoting and cold-racing.");
                                var failKind = !fastProbed ? StrategyFailureKind.PlaybackFailed : StrategyFailureKind.Unknown;
                                RecordStrategyFailure(memKey, specific.Name, failKind);
                                if (failKind == StrategyFailureKind.PlaybackFailed)
                                    _eventBus?.PublishStrategyDemoted(specific.Name, memKey, "Pre-flight probe rejected URL", ctx.CorrelationId);
                                if (fastRes != null && fastProbed && (bestSoFar == null || (fastRes.Height ?? 0) > (bestSoFar.Height ?? 0)))
                                { bestSoFar = fastRes; bestSoFarTier = specific.Name; }
                                remembered = null; rememberedGroup = null;
                                cascadeStart = 0; // re-enable cold race + full sequential cascade
                            }
                        }
                        else
                        {
                            // Either the remembered strategy no longer exists in the catalog (code
                            // change) or the user explicitly disabled it via config. Either way,
                            // clear the memory pointer and let the cold race pick a replacement.
                            bool userDisabled = IsStrategyDisabled(remembered.StrategyName, rememberedGroup, disabled);
                            _logger.Debug("[" + ctx.CorrelationId + "] [StrategyMemory] Remembered strategy '"
                                + remembered.StrategyName + "' "
                                + (userDisabled ? "is disabled by user config" : "not in current catalog")
                                + " â€” cold-racing instead.");
                            remembered = null; rememberedGroup = null;
                        }
                    }

                    // Cold-start race: no memory, Tier 1 is first, Tier 2 is also active. Race every
                    // applicable Tier 1 variant plus Tier 2 in parallel â€” concurrency capped. First past
                    // the quality floor wins. Sub-floor results are kept as fallback. This is the
                    // "request spam, resolve fast, then remember" approach the user asked for.
                    bool coldStart = remembered == null;
                    int tier1Idx = cascade.IndexOf("tier1");
                    int tier2Idx = cascade.IndexOf("tier2");
                    bool canRace = coldStart && tier1Idx == cascadeStart && tier2Idx > tier1Idx;
                    if (canRace)
                    {
                        var strategies = BuildColdRaceStrategies(targetUrl, player, payload.Args, disabled);
                        strategies = OrderStrategiesForRace(strategies, memKey);
                        string hostForBudget = HostFromUrl(targetUrl);
                        bool useWaves = _settings.Config.EnableWaveRace;
                        int waveSize = Math.Max(1, _settings.Config.WaveSize);
                        int stageMs = Math.Max(1, _settings.Config.WaveStageDeadlineSeconds) * 1000;

                        _logger.Info("[" + ctx.CorrelationId + "] [Race] Cold-start across " + strategies.Count + " strategies: ["
                            + string.Join(", ", strategies.Select(s => s.Name)) + "]" +
                            (useWaves
                                ? " (waves of " + waveSize + ", " + (stageMs / 1000.0).ToString("0.0") + "s per stage, budget=" + _settings.Config.PerHostRequestBudget + "/" + _settings.Config.PerHostRequestWindowSeconds + "s per host)"
                                : " (legacy mode: fire all at once, first past " + floorH + "p floor wins)"));
                        var raceSw = Stopwatch.StartNew();

                        using var raceCts = new System.Threading.CancellationTokenSource();
                        using var sem = new System.Threading.SemaphoreSlim(useWaves ? Math.Max(waveSize + 1, 3) : 5);
                        var sctx = new StrategyRunContext(targetUrl, player, payload.Args, ctx, floorH, raceCts.Token);

                        // Pending in-flight tasks. In legacy mode all strategies are launched up
                        // front; in wave mode we launch `waveSize` at a time and kick off the next
                        // wave after stageMs elapses (or immediately if all pending tasks completed).
                        var pending = new List<Task<(ResolveStrategy Strategy, YtDlpResult? Result)>>();
                        int nextIdx = 0;
                        int waveNumber = 0;
                        int budgetSkipped = 0;

                        // Launch helper: launches up to N more strategies, honouring the per-host
                        // tier-1 budget (cloud strategies bypass it). Returns the count launched.
                        int LaunchMore(int max)
                        {
                            int launched = 0;
                            while (launched < max && nextIdx < strategies.Count && winnerResult == null)
                            {
                                var s = strategies[nextIdx++];
                                bool countsAgainstBudget = s.Group == "tier1";
                                if (countsAgainstBudget && !TryConsumeHostBudget(hostForBudget))
                                {
                                    budgetSkipped++;
                                    _logger.Warning("[" + ctx.CorrelationId + "] [Race] Per-host budget for " + hostForBudget + " exhausted â€” skipping '" + s.Name + "' (rate-limit guard).");
                                    continue;
                                }
                                pending.Add(RunStrategySlot(s, sctx, sem, raceCts.Token));
                                launched++;
                            }
                            if (launched > 0)
                            {
                                waveNumber++;
                                _logger.Debug("[" + ctx.CorrelationId + "] [Race/Wave " + waveNumber + "] Launched " + launched + " strategy(ies). " +
                                    "Pending=" + pending.Count + " Remaining=" + (strategies.Count - nextIdx));
                            }
                            return launched;
                        }

                        if (useWaves) LaunchMore(waveSize);
                        else LaunchMore(strategies.Count);

                        while (pending.Count > 0 && winnerResult == null)
                        {
                            Task<Task<(ResolveStrategy, YtDlpResult?)>> anyCompleted = Task.WhenAny(pending);
                            Task stageTimer = useWaves ? Task.Delay(stageMs) : Task.Delay(-1);
                            await Task.WhenAny(anyCompleted, stageTimer);

                            // Drain any tasks that finished. Multiple can complete in one pass.
                            for (int pi = pending.Count - 1; pi >= 0; pi--)
                            {
                                if (!pending[pi].IsCompleted) continue;
                                var finished = pending[pi];
                                pending.RemoveAt(pi);
                                var (strat, res) = await finished;
                                if (res == null)
                                {
                                    if (!raceCts.IsCancellationRequested) RecordStrategyFailure(memKey, strat.Name);
                                    continue;
                                }
                                if (_settings.Config.EnablePreflightProbe && !strat.Group.StartsWith("tier1"))
                                {
                                    bool reachable = await CheckUrlReachable(res.Url, ctx);
                                    if (!reachable)
                                    {
                                        _logger.Warning("[" + ctx.CorrelationId + "] [Race] '" + strat.Name + "' URL failed pre-flight probe â€” demoting and skipping.");
                                        RecordStrategyFailure(memKey, strat.Name, StrategyFailureKind.PlaybackFailed);
                                        _eventBus?.PublishStrategyDemoted(strat.Name, memKey, "Pre-flight probe rejected URL", ctx.CorrelationId);
                                        continue;
                                    }
                                }
                                if (IsAcceptableQuality(res.Height, floorH))
                                {
                                    if (winnerResult != null)
                                    {
                                        // Earlier task in this same drain pass already won. Record
                                        // the also-ran's success for memory ranking purposes, but
                                        // do NOT overwrite the winner â€” otherwise a second-place
                                        // strategy that happened to finish milliseconds later
                                        // would replace the true first-past-the-post result. This
                                        // is the "double winner" bug that previously caused a
                                        // demoted strategy's URL to replace cloud's on retry.
                                        _logger.Debug("[" + ctx.CorrelationId + "] [Race] '" + strat.Name + "' also succeeded in the same pass, but '" + activeStrategy + "' already won â€” discarding.");
                                        RecordStrategySuccess(memKey, strat.Name, res.Height);
                                        continue;
                                    }
                                    winnerResult = res; activeTier = strat.Group; activeStrategy = strat.Name;
                                    winnerForcesRelayWrap = strat.ForceRelayWrap;
                                    _logger.Info("[" + ctx.CorrelationId + "] [Race] '" + strat.Name + "' cleared floor in " + raceSw.ElapsedMilliseconds + "ms â€” winner.");
                                    try { raceCts.Cancel(); } catch { }

                                    // Sweep already-completed pending tasks so StrategyMemory sees their outcomes.
                                    // Without this, a loser that finished BEFORE the winner (e.g. tier1:ipv6 dying
                                    // fast on a getaddrinfo failure) is silently dropped â€” the demote threshold
                                    // never fires and cold-start re-runs the same broken strategy forever.
                                    // IsCompleted == true here means the slot ran under its own steam, so the
                                    // outcome is genuine and we bypass the cancellation guard at the top of the
                                    // for-loop.
                                    for (int sj = pending.Count - 1; sj >= 0; sj--)
                                    {
                                        if (!pending[sj].IsCompleted) continue;
                                        var sweptTask = pending[sj];
                                        pending.RemoveAt(sj);
                                        var (sweptStrat, sweptRes) = await sweptTask;
                                        if (sweptRes == null)
                                        {
                                            RecordStrategyFailure(memKey, sweptStrat.Name);
                                        }
                                        else
                                        {
                                            _logger.Debug("[" + ctx.CorrelationId + "] [Race] '" + sweptStrat.Name + "' also succeeded but '" + activeStrategy + "' already won â€” discarding.");
                                            RecordStrategySuccess(memKey, sweptStrat.Name, sweptRes.Height);
                                        }
                                    }
                                    break; // stop draining â€” any still-pending tasks are mid-cancellation.
                                }
                                else
                                {
                                    _logger.Info("[" + ctx.CorrelationId + "] [Race] '" + strat.Name + "' returned " + res.Height + "p < " + floorH + "p floor â€” keeping as fallback.");
                                    if (bestSoFar == null || (res.Height ?? 0) > (bestSoFar.Height ?? 0))
                                    { bestSoFar = res; bestSoFarTier = strat.Name; }
                                }
                            }

                            // If the stage timer fired (no completion), launch the next wave.
                            // If everything completed this pass with no winner, also launch more.
                            if (useWaves && winnerResult == null && nextIdx < strategies.Count)
                            {
                                if (stageTimer.IsCompleted || pending.Count == 0)
                                {
                                    if (LaunchMore(waveSize) == 0 && pending.Count == 0)
                                    {
                                        // All remaining were budget-skipped â€” nothing left to wait for.
                                        break;
                                    }
                                }
                            }
                        }
                        raceSw.Stop();

                        if (budgetSkipped > 0)
                        {
                            _logger.Info("[" + ctx.CorrelationId + "] [Race] " + budgetSkipped + " tier-1 strategy(ies) skipped due to per-host budget.");
                        }

                        // Skip past the groups we covered in the race; Tier 3 is the remaining sequential fallback.
                        if (winnerResult == null)
                        {
                            cascadeStart = Math.Max(cascadeStart, tier2Idx + 1);
                            _logger.Debug("[" + ctx.CorrelationId + "] [Race] No winner â€” continuing cascade at index " + cascadeStart + ".");
                        }
                    }

                    int i = cascadeStart;
                    bool cascadeRestarted = false;
                    while (i < cascade.Count && winnerResult == null)
                    {
                        string tier = cascade[i];
                        YtDlpResult? tierResult = null;
                        string tierStrategy = tier + ":default";
                        if (tier == "tier1") tierStrategy = "tier1:default";
                        else if (tier == "tier2") tierStrategy = "tier2:cloud-whyknot";
                        else if (tier == "tier3") tierStrategy = "tier3:plain";

                        // Honour specific-strategy disables in the sequential fallback too. If the
                        // user disabled "tier2:cloud-whyknot" but kept "tier2" active, we have no
                        // other tier-2 strategy to try â€” so this tier is effectively muted.
                        if (IsStrategyDisabled(tierStrategy, tier, disabled))
                        {
                            _logger.Debug("[" + ctx.CorrelationId + "] [" + tier + "] Skipped â€” '" + tierStrategy + "' is disabled by user config.");
                            i++;
                            continue;
                        }

                        if (tier == "tier1") { tierResult = await AttemptTier1(targetUrl, player, ctx); }
                        else if (tier == "tier2") { tierResult = await AttemptTier2(targetUrl, player, ctx); }
                        else if (tier == "tier3") { tierResult = await AttemptTier3(payload.Args, ctx); }

                        if (tierResult != null)
                        {
                            // Pre-handoff probe: verify the URL is actually reachable before we
                            // commit to it. AttemptTier1 already probes its own results, but Tier 2
                            // (cloud) and Tier 3 (yt-dlp-og) previously trusted whatever the tier
                            // returned â€” producing false "successes" that poisoned StrategyMemory.
                            // Skip if config opts out, or if the probe has already run (Tier 1).
                            if (_settings.Config.EnablePreflightProbe && tier != "tier1")
                            {
                                bool reachable = await CheckUrlReachable(tierResult.Url, ctx);
                                if (!reachable)
                                {
                                    _logger.Warning("[" + ctx.CorrelationId + "] [" + tier + "] URL returned but pre-flight probe rejected it â€” demoting '" + tierStrategy + "' and cascading.");
                                    RecordStrategyFailure(memKey, tierStrategy, StrategyFailureKind.PlaybackFailed);
                                    _eventBus?.PublishStrategyDemoted(tierStrategy, memKey, "Pre-flight probe rejected URL", ctx.CorrelationId);
                                    tierResult = null;
                                }
                            }
                        }
                        if (tierResult != null)
                        {
                            // Quality heuristic: accept immediately if height is unknown (tier 2/3/streamlink)
                            // or >= floor. Otherwise record as best-so-far and keep cascading.
                            if (IsAcceptableQuality(tierResult.Height, floorH))
                            {
                                winnerResult = tierResult; activeTier = tier; activeStrategy = tierStrategy;
                                break;
                            }

                            _logger.Info("[" + ctx.CorrelationId + "] [" + tier + "] returned " + tierResult.Height + "p < " + floorH + "p floor â€” cascading to next tier for better quality.");
                            if (bestSoFar == null || (tierResult.Height ?? 0) > (bestSoFar.Height ?? 0))
                            {
                                // Record the full strategy name (e.g. "tier2:cloud-whyknot"), not just "tier2".
                                // The best-of fallback at the end of the cascade feeds this name into StrategyMemory â€”
                                // using the group alone would synthesize a fake "tier2:default" entry that never ran.
                                bestSoFar = tierResult; bestSoFarTier = tierStrategy;
                            }
                        }
                        else if (remembered != null && tier == rememberedGroup)
                        {
                            RecordStrategyFailure(memKey, remembered.StrategyName);
                        }

                        if (!cascadeRestarted && remembered != null && tier == rememberedGroup)
                        {
                            _logger.Warning("[" + ctx.CorrelationId + "] [StrategyMemory] Remembered group '" + rememberedGroup + "' failed â€” retrying full cascade.");
                            remembered = null; rememberedGroup = null; cascadeRestarted = true;
                            i = 0; continue;
                        }

                        i++;
                    }

                    // All tiers finished below floor: fall back to the best sub-floor result we got.
                    if (winnerResult == null && bestSoFar != null)
                    {
                        _logger.Warning("[" + ctx.CorrelationId + "] All tiers returned below the " + floorH + "p floor; using best-of: [" + bestSoFarTier + "] at " + bestSoFar.Height + "p.");
                        winnerResult = bestSoFar; activeTier = bestSoFarTier.Split(':')[0]; activeStrategy = bestSoFarTier.Contains(":") ? bestSoFarTier : (bestSoFarTier + ":default");
                    }

                    if (winnerResult != null && !string.IsNullOrEmpty(activeStrategy))
                    {
                        RecordStrategySuccess(memKey, activeStrategy, winnerResult.Height);
                        finalStrategy = activeStrategy;
                        _logger.Debug("[" + ctx.CorrelationId + "] [StrategyMemory] Recorded success for '" + activeStrategy + "' on " + memKey +
                            (winnerResult.Height is int h ? " at " + h + "p." : "."));
                    }
                }

                // Tier 4 is guaranteed enabled (clamped in `disabled` construction above), so this
                // branch always fires when everything above failed. Returning the original URL is
                // the last-resort "something is better than nothing" contract â€” AVPro may still
                // reject it, but the program never returns null/empty.
                if (winnerResult == null)
                {
                    _logger.Warning("[" + ctx.CorrelationId + "] All active tiers exhausted â€” falling back to original URL (passthrough). Video may not play correctly.");
                    winnerResult = new YtDlpResult(targetUrl, null, null, null, null, null);
                    activeTier = "tier4";
                    _eventBus?.PublishError("ResolutionEngine", new ErrorContext {
                        Category = ErrorCategory.Network,
                        Code = ErrorCodes.ALL_TIERS_FAILED,
                        Summary = "All resolution tiers failed",
                        Detail = "Tried: " + string.Join(", ", cascade) + ". All failed or returned unreachable URLs for: " + targetUrl,
                        ActionHint = "Check your internet connection. The video URL may be geo-restricted or require authentication.",
                        IsRecoverable = true
                    }, ctx.CorrelationId);

                    // Anonymous reporting hook. Fire-and-forget â€” the user doesn't wait on the
                    // report. ReportingService internally checks the opt-in flag, gates on a
                    // first-launch prompt if the user hasn't answered yet, and rate-limits.
                    if (_reporting != null)
                    {
                        var failureCtx = new CascadeFailureContext
                        {
                            OriginalUrl = targetUrl,
                            Player = player,
                            ErrorSummary = "All resolution tiers failed. Tried: " + string.Join(", ", cascade) + ".",
                        };
                        _ = Task.Run(async () =>
                        {
                            try { await _reporting.ReportCascadeFailureAsync(failureCtx); }
                            catch (Exception rex) { _logger.Debug("[Reporting] Background send threw: " + rex.Message); }
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("[" + ctx.CorrelationId + "] Resolution loop fatal error: " + ex.Message, ex);
            winnerResult = new YtDlpResult(targetUrl, null, null, null, null, null);
            activeTier = "tier4-error";
        }

        string? result = winnerResult?.Url;
        bool isLive = result != null && (result.Contains(".m3u8") || result.Contains("m3u8"));
        string streamType = isLive ? "live" : (!string.IsNullOrEmpty(result) && result != "FAILED" ? "vod" : "unknown");

        if (isLive)
            _logger.Info("[" + ctx.CorrelationId + "] Detected HLS/live stream. Stream type: " + streamType);

        if (!string.IsNullOrEmpty(result) && result != "FAILED")
            result = ApplyRelayWrap(result, skipRelayWrap, ctx.CorrelationId, forceWrap: winnerForcesRelayWrap);

        // Populate resolve cache on successful non-passthrough resolution. Skipping Tier 4 means
        // a fresh cascade attempt next time â€” we don't want to "remember" a failed cascade.
        if (winnerResult != null && !activeTier.StartsWith("tier4"))
        {
            _resolveCache[cacheKey] = new ResolveCacheEntry(winnerResult, activeTier, DateTime.UtcNow.Add(ResolveCacheTtl), 0);
            if (_resolveCache.Count > 100)
            {
                var now = DateTime.UtcNow;
                foreach (var kv in _resolveCache)
                    if (kv.Value.Expires <= now) _resolveCache.TryRemove(kv.Key, out _);
            }
        }

        // Record in the recent-resolutions ring so an AVPro "Loading failed" can demote the
        // responsible strategy and flip this history entry's PlaybackVerified flag. Skip for
        // tier4 passthrough (no strategy, PlaybackVerified stays null).
        bool canTrackPlayback = winnerResult != null && !activeTier.StartsWith("tier4")
            && !string.IsNullOrEmpty(finalStrategy) && !string.IsNullOrEmpty(finalMemKey)
            && !string.IsNullOrEmpty(result);

        var tierKey = activeTier.Split('-')[0];
        _tierCounts.AddOrUpdate(tierKey, 1, (_, v) => v + 1);

        var entry = new HistoryEntry {
            Timestamp = DateTime.Now,
            OriginalUrl = targetUrl,
            ResolvedUrl = result ?? "FAILED",
            Tier = activeTier,
            Player = historyPlayerLabel ?? player,
            Success = !string.IsNullOrEmpty(result),
            IsLive = isLive,
            StreamType = streamType,
            ResolutionHeight = winnerResult?.Height,
            ResolutionWidth = winnerResult?.Width,
            Vcodec = winnerResult?.Vcodec,
            // Null = pending verification; RecordRecentResolution will flip to true after the
            // verify delay, or HandleAvProLoadFailure to false on an AVPro reject. Tier4 entries
            // stay null (nothing to verify against).
            PlaybackVerified = canTrackPlayback ? (bool?)null : null
        };

        _settings.Config.History.Insert(0, entry);
        if (_settings.Config.History.Count > 100) _settings.Config.History.RemoveAt(100);
        try { _settings.Save(); }
        catch (Exception ex) { _logger.Warning("[" + ctx.CorrelationId + "] Failed to persist history after resolution: " + ex.Message); }

        if (canTrackPlayback)
        {
            // Pass the pre-wrap upstream URL too â€” the relay-abort detector reports that, not
            // the wrapped /play?target=â€¦ URL the player actually fetched.
            RecordRecentResolution(targetUrl, result!, finalStrategy, finalMemKey, ctx.CorrelationId, entry, upstreamUrl: winnerResult!.Url);
        }

        resolutionSw.Stop();
        Interlocked.Decrement(ref _activeResolutions);
        UpdateStatus("Resolution completed via " + activeTier.ToUpper(), ctx);

        string resolutionLabel = "";
        if (winnerResult != null && winnerResult.Height.HasValue)
        {
            string w = winnerResult.Width?.ToString() ?? "?";
            string v = winnerResult.Vcodec != null ? " " + winnerResult.Vcodec : "";
            resolutionLabel = " [" + w + "x" + winnerResult.Height + v + "]";
        }
        _logger.Success("[" + ctx.CorrelationId + "] Final Resolution [" + activeTier + "] [" + streamType + "]" + resolutionLabel + " in " + resolutionSw.ElapsedMilliseconds + "ms: " + (result != null && result.Length > 100 ? result.Substring(0, 100) + "..." : result));
        return result;
    }

    private static string FormatMetaLog(YtDlpResult r)
    {
        if (r.Height == null && r.Vcodec == null) return "";
        string h = r.Height.HasValue ? r.Height + "p" : "?";
        string v = r.Vcodec != null ? " " + r.Vcodec : "";
        return " [" + h + v + "]";
    }

    // Per-tier attempt wrappers: call resolver with timing, run reachability check where applicable,
    // emit the success/failure log line. Return the YtDlpResult if successful, null otherwise.
    // Shared between sequential cascade and parallel race branch.
    // Channel live patterns: /channel/UCxxx/live, /c/Name/live, /user/Name/live, /@handle/live.
}
