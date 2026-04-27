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

[SupportedOSPlatform("windows")]
public class ResolutionEngine
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
    // Seed value only — Phase 3 will capture the actual UA from incoming AVPro relay requests.
    internal const string VrchatAvProUserAgent = "UnityPlayer/2022.3.22f1 (UnityWebRequest/1.0, libcurl/7.84.0-DEV)";
    internal const string VrchatReferer = "https://vrchat.com/";

    // Domain-level "requires PO token" flag. YouTube's bot-detection mode is not per-video — once it
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

    // Positive resolve cache: short-TTL (URL, player) → resolved URL. VRChat calls yt-dlp multiple times
    // for the same video (thumbnail probe + duration probe + actual play). Without this, each call hits
    // whyknot.dev fresh, burning ~6s per trip. Cache TTL stays short to keep CDN URLs (which expire
    // server-side) fresh and lets transient failures self-heal on the next real play.
    //
    // MaxHits caps replays: 2-3 calls per play-event is the normal VRChat pattern, but AVPro retrying
    // a broken URL shows up as 4+ calls within the TTL. After MaxHits, the entry self-invalidates so
    // the next attempt re-resolves — previously we'd keep serving a stale URL for the full 90s window
    // even when AVPro was obviously bouncing off it (e.g. SoundCloud signed-URL expiry).
    private record ResolveCacheEntry(YtDlpResult Result, string Tier, DateTime Expires, int Hits);
    private readonly ConcurrentDictionary<string, ResolveCacheEntry> _resolveCache = new();
    private static readonly TimeSpan ResolveCacheTtl = TimeSpan.FromSeconds(90);
    private const int ResolveCacheMaxHits = 3;
    public static string ResolveCacheKey(string url, string player) => player + "|" + url;

    // Short-term record of what each recent resolution handed back. Keyed by BOTH the outgoing
    // resolved URL (post-wrap) AND the original user URL — VRChat's AVPro "Opening" log line can
    // reference either depending on whether VRChat's own yt-dlp hook intercepted. When
    // VrcLogMonitor fires OnAvProLoadFailure with an URL, we look it up here to find the strategy
    // that produced it, then demote that strategy with PlaybackFailed (one-strike demote).
    //
    // HistoryEntryRef lets us flip the matching history row's PlaybackVerified flag on the
    // feedback signal — without it, the UI's Success column would still lie about dead URLs.
    private record RecentResolution(string StrategyName, string MemKey, string OriginalUrl, string ResolvedUrl, string? UpstreamUrl, DateTime CreatedAt, string CorrelationId, HistoryEntry? HistoryEntryRef);
    private readonly ConcurrentDictionary<string, RecentResolution> _recentByUrl = new();
    private static readonly TimeSpan RecentResolutionTtl = TimeSpan.FromSeconds(60);
    private const int RecentResolutionCap = 64;

    // Per-host rolling-window budget for tier-1 (yt-dlp) spawns. Prevents the cold race from
    // machine-gunning a single host (e.g. YouTube) when the user plays videos in quick succession
    // or iterates on builds. When budget is exhausted for a host, tier-1 strategies are skipped
    // and the race falls through to tier-2 cloud — which egresses from a different IP and doesn't
    // count against the local-IP rate limit. Budget size + window are config-driven
    // (PerHostRequestBudget / PerHostRequestWindowSeconds).
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hostRequestLog = new();
    private readonly object _hostRequestLogLock = new();
    // How long to wait before promoting a history entry from "pending" to "verified". AVPro
    // typically logs "Loading failed" within 1-3 seconds of Opening; 8s is comfortable headroom
    // without making the UI feel stuck.
    private static readonly TimeSpan PlaybackVerifyDelay = TimeSpan.FromSeconds(8);

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
        // fast-path. Playback failure → PlaybackFailed → one-strike demote (see StrategyMemory).
        _monitor.OnAvProLoadFailure += HandleAvProLoadFailure;

        LogActiveTiers();
    }

    // Subscribe to RelayServer's OnClientAbortedEarly so Unity playback failures (which don't
    // emit AVPro error lines) also feed the playback-feedback demotion loop. Called once at
    // startup after both services are constructed. Keeps RelayServer unaware of the engine.
    public void AttachRelayAbortDetector(RelayServer relayServer)
    {
        if (relayServer == null) return;
        relayServer.OnClientAbortedEarly += HandleRelayClientAbort;
    }

    // Rolling-window tracker of "player aborted mid-stream before playback could have started"
    // events, keyed by the UPSTREAM target URL (not the relay-wrapped URL the player fetched).
    // When the same target URL is aborted `RelayAbortThreshold` times inside the window, treat
    // it as a Unity PlaybackFailed — demote the strategy that produced it, evict the cache, and
    // fire the same StrategyDemoted event the AVPro path uses. Mirrors VRChat's retry cadence:
    // Unity reopens a failing URL every 5-8s, so 3 aborts inside 30s is a reliable signal with
    // zero false positives on normal playback (normal playback never aborts short).
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _relayAbortLog = new();
    private readonly object _relayAbortLogLock = new();
    private static readonly TimeSpan RelayAbortWindow = TimeSpan.FromSeconds(30);
    private const int RelayAbortThreshold = 3;

    private void HandleRelayClientAbort(string targetUrl, long bytesAtAbort)
    {
        if (string.IsNullOrEmpty(targetUrl)) return;
        var now = DateTime.UtcNow;
        var cutoff = now - RelayAbortWindow;
        Queue<DateTime> queue;
        int abortCountInWindow;
        lock (_relayAbortLogLock)
        {
            queue = _relayAbortLog.GetOrAdd(targetUrl, _ => new Queue<DateTime>());
            while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
            queue.Enqueue(now);
            abortCountInWindow = queue.Count;
        }

        _logger.Debug("[Playback] Relay abort for " + (targetUrl.Length > 80 ? targetUrl.Substring(0, 80) + "..." : targetUrl) +
            " (bytes=" + bytesAtAbort + ", count in " + RelayAbortWindow.TotalSeconds + "s window=" + abortCountInWindow + "/" + RelayAbortThreshold + ")");

        if (abortCountInWindow < RelayAbortThreshold) return;

        // Threshold hit — the player has repeatedly rejected this URL's format. Reuse the AVPro
        // failure handler, which already knows how to: match the URL to a recent resolution,
        // demote the strategy with PlaybackFailed, evict resolve cache, publish the event, and
        // flip the history entry's PlaybackVerified flag.
        lock (_relayAbortLogLock)
        {
            _relayAbortLog.TryRemove(targetUrl, out _);
        }
        _logger.Warning("[Playback] [Relay] Unity/AVPro aborted " + abortCountInWindow + "× within " + RelayAbortWindow.TotalSeconds + "s on " +
            (targetUrl.Length > 80 ? targetUrl.Substring(0, 80) + "..." : targetUrl) + " — treating as PlaybackFailed.");
        HandleAvProLoadFailure(targetUrl, now);
    }

    private void HandleAvProLoadFailure(string failedUrl, DateTime observedAt)
    {
        if (string.IsNullOrWhiteSpace(failedUrl)) return;
        PruneRecentResolutions();
        if (!_recentByUrl.TryGetValue(failedUrl, out var recent))
        {
            _logger.Debug("[Playback] AVPro Loading failed for URL not in recent-resolutions ring (" +
                (failedUrl.Length > 80 ? failedUrl.Substring(0, 80) + "..." : failedUrl) + "). Ignoring — likely a URL WKVRCProxy did not resolve.");
            return;
        }
        if (observedAt - recent.CreatedAt > RecentResolutionTtl)
        {
            _logger.Debug("[Playback] AVPro failure for resolved URL older than TTL — not demoting.");
            return;
        }
        _logger.Warning("[Playback] [" + recent.CorrelationId + "] AVPro rejected resolved URL from '" + recent.StrategyName + "' on " + recent.MemKey + ". Demoting (PlaybackFailed) — next request will re-cascade.");
        RecordStrategyFailure(recent.MemKey, recent.StrategyName, StrategyFailureKind.PlaybackFailed);
        _eventBus?.PublishStrategyDemoted(recent.StrategyName, recent.MemKey, "AVPro rejected URL", recent.CorrelationId);

        // Flag the matching history row so the UI's Success column reflects actual playback, not
        // just "resolution returned a URL". If the scheduled verifier hasn't run yet, this wins
        // over it because it re-reads the flag under a null check.
        if (recent.HistoryEntryRef != null && recent.HistoryEntryRef.PlaybackVerified != false)
        {
            recent.HistoryEntryRef.PlaybackVerified = false;
            try { _settings.Save(); }
            catch (Exception ex) { _logger.Debug("[Playback] Failed to persist playback-failed flag: " + ex.Message); }
        }

        // Evict any resolve-cache entry that would replay this dead URL on the duration/thumbnail
        // probes that follow an initial play-attempt. Without this the cache would serve the same
        // URL for up to 90s and AVPro would keep failing.
        foreach (var cacheKey in _resolveCache.Keys.ToList())
        {
            if (_resolveCache.TryGetValue(cacheKey, out var entry)
                && (entry.Result.Url == recent.ResolvedUrl || cacheKey.EndsWith("|" + recent.OriginalUrl)))
            {
                _resolveCache.TryRemove(cacheKey, out _);
            }
        }
        // And clear the recent-resolutions entry so a second "Loading failed" for the same URL
        // doesn't double-demote.
        _recentByUrl.TryRemove(failedUrl, out _);
        if (recent.ResolvedUrl != failedUrl) _recentByUrl.TryRemove(recent.ResolvedUrl, out _);
        if (recent.OriginalUrl != failedUrl) _recentByUrl.TryRemove(recent.OriginalUrl, out _);
        if (!string.IsNullOrEmpty(recent.UpstreamUrl) && recent.UpstreamUrl != failedUrl)
            _recentByUrl.TryRemove(recent.UpstreamUrl!, out _);
    }

    private void RecordRecentResolution(string originalUrl, string resolvedUrl, string strategyName, string memKey, string correlationId, HistoryEntry? historyEntry = null, string? upstreamUrl = null)
    {
        if (string.IsNullOrEmpty(strategyName) || string.IsNullOrEmpty(memKey)) return;
        var rec = new RecentResolution(strategyName, memKey, originalUrl, resolvedUrl, upstreamUrl, DateTime.UtcNow, correlationId, historyEntry);
        _recentByUrl[originalUrl] = rec;
        if (!string.Equals(resolvedUrl, originalUrl, StringComparison.Ordinal))
            _recentByUrl[resolvedUrl] = rec;
        // Relay-abort detector reports the *upstream* URL (decoded `target` param), not the
        // wrapped /play?target=… URL the player sees. Without this third key, threshold-hit
        // aborts on relay-wrapped strategies (tier2 cloud, etc.) miss the ring lookup and
        // never demote — the resolve cache then keeps serving the same dead URL.
        if (!string.IsNullOrEmpty(upstreamUrl)
            && !string.Equals(upstreamUrl, originalUrl, StringComparison.Ordinal)
            && !string.Equals(upstreamUrl, resolvedUrl, StringComparison.Ordinal))
            _recentByUrl[upstreamUrl] = rec;
        PruneRecentResolutions();

        // Schedule the optimistic "verified" promotion. If no AVPro failure arrives within the
        // delay, we promote to true — a.k.a. "no news is good news". A failure observed in the
        // meantime sets PlaybackVerified=false and this task finds it already non-null and bails.
        if (historyEntry != null && historyEntry.PlaybackVerified == null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(PlaybackVerifyDelay);
                if (historyEntry.PlaybackVerified == null)
                {
                    historyEntry.PlaybackVerified = true;
                    try { _settings.Save(); } catch { /* best-effort persistence */ }
                }
            });
        }
    }

    private void PruneRecentResolutions()
    {
        var now = DateTime.UtcNow;
        if (_recentByUrl.Count <= RecentResolutionCap)
        {
            foreach (var kv in _recentByUrl)
                if (now - kv.Value.CreatedAt > RecentResolutionTtl) _recentByUrl.TryRemove(kv.Key, out _);
            return;
        }
        // Hard cap hit: drop oldest until under cap, plus any past TTL.
        var ordered = _recentByUrl.OrderBy(kv => kv.Value.CreatedAt).ToList();
        foreach (var kv in ordered)
        {
            if (_recentByUrl.Count <= RecentResolutionCap && now - kv.Value.CreatedAt <= RecentResolutionTtl) break;
            _recentByUrl.TryRemove(kv.Key, out _);
        }
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

    // Probe headers are shaped to look like the first request AVPro/UnityPlayer sends when it
    // opens a stream — NOT like a scanner. Anti-bot CDNs (YouTube, Cloudflare, Akamai) fingerprint
    // probe-like requests (no UA, HEAD, Range: bytes=0-0, empty Accept) and return 403 for ones
    // that look synthetic. We send: AVPro's real UA, a realistic Accept set, gzip/identity encoding
    // (AVPro doesn't compress range reads), and a DASH/MP4-typical initial segment range. Combined
    // with curl-impersonate's Chrome TLS fingerprint, the request is indistinguishable from an
    // actual playback start. Keep these changes in lockstep with any UA/header tweaks made
    // elsewhere — inconsistency is itself a fingerprint.
    private static Dictionary<string, string> BuildBinaryProbeHeaders() => new()
    {
        ["User-Agent"] = VrchatAvProUserAgent,
        ["Accept"] = "*/*",
        ["Accept-Language"] = "en-US,en;q=0.9",
        ["Accept-Encoding"] = "identity;q=1, *;q=0",
        ["Range"] = "bytes=0-4095",
        ["Connection"] = "keep-alive",
    };

    private static Dictionary<string, string> BuildHlsProbeHeaders() => new()
    {
        // HLS manifests: AVPro fetches them as plain GETs, typically with a browser-shaped UA
        // (some AVPro builds use the OS WebView UA for manifest fetches, others use UnityPlayer).
        // UnityPlayer UA is the more conservative choice — matches what the native-UA deny-list
        // hosts expect anyway.
        ["User-Agent"] = VrchatAvProUserAgent,
        ["Accept"] = "application/vnd.apple.mpegurl, application/x-mpegurl, */*;q=0.8",
        ["Accept-Language"] = "en-US,en;q=0.9",
        ["Accept-Encoding"] = "gzip, deflate, identity",
        ["Connection"] = "keep-alive",
    };

    // Verify a resolved URL is reachable before accepting it. Designed to be indistinguishable
    // from AVPro's own first fetch so CDNs that 403 probes won't fingerprint us.
    // - Binary streams (MP4, DASH, proxy URLs): Range: bytes=0-4095 — same as a real initial
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
                // Probe timed out or process error — cannot confirm reachability, but do not reject.
                // Streaming servers (e.g. private HLS, proxy URLs) often do not respond to probe
                // requests within 5s. Rejecting on timeout causes valid URLs to cascade needlessly.
                _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: curl-impersonate timed out for " + shortUrl + " — accepting URL (benefit of the doubt).");
                return true;
            }
            bool reachable = status is (>= 200 and < 400) or 416;
            if (!reachable)
                _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: curl-impersonate returned HTTP " + status + " [" + probeMode + "] for " + shortUrl);
            else
                _logger.Debug("[" + ctx.CorrelationId + "] Reachability check: curl-impersonate HTTP " + status + " — OK for " + shortUrl);
            return reachable;
        }

        // Fallback: plain HttpClient with AVPro-shaped headers. Same request shape curl-impersonate
        // would send, minus the Chrome TLS fingerprint. Some CDNs will still 403 the plain .NET
        // handshake — we accept on timeout to avoid false-negative cascading.
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
                _logger.Debug("[" + ctx.CorrelationId + "] Reachability check: HttpClient HTTP " + status + " — OK for " + shortUrl);
            return reachable;
        }
        catch (OperationCanceledException)
        {
            // Probe timed out — accept with warning rather than rejecting a potentially valid URL.
            _logger.Warning("[" + ctx.CorrelationId + "] Reachability check: HttpClient timed out for " + shortUrl + " — accepting URL (benefit of the doubt).");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning("[" + ctx.CorrelationId + "] Reachability check error (" + ex.GetType().Name + ") [" + probeMode + "] for " + shortUrl + " — " + ex.Message);
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

    // Normalize a URL's host for domain-key lookup ("www.youtube.com" → "youtube.com", "youtu.be" stays).
    // Returns the empty string if the URL is malformed.
    public static string ExtractHost(string url)
    {
        try
        {
            string host = new Uri(url).Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            return host;
        }
        catch { return ""; }
    }

    // Scans yt-dlp stderr for well-known YouTube bot-detection phrases. YouTube emits a curly
    // right single quote (U+2019) in "you're"; normalize to the straight ASCII apostrophe so a
    // single set of literals covers both the canonical and the wire form. Without this, the
    // detector silently misses every real bot-detection error and MarkDomainRequiresPot never
    // fires, so the PO-upgrade flywheel stays cold for youtube.com.
    public static bool IsBotDetectionStderr(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        string normalized = stderr.Replace('\u2019', '\'');
        return normalized.Contains("Sign in to confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Sign in to confirm you are not a bot", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase);
    }

    // Build the yt-dlp CLI fragment that wires in the bgutil yt-dlp plugin. yt-dlp loads the plugin
    // from pluginDir and uses it to resolve the youtubepot-bgutilhttp: extractor-arg by calling the
    // sidecar at http://localhost:{potPort} at request time. The plugin mints PO tokens bound to
    // yt-dlp's own visitor_data, which is the only binding YouTube actually accepts.
    //
    // Returns an empty list when inputs are not ready (no port, no plugin dir) — caller is expected
    // to have already confirmed the plugin dir exists on disk; this helper is pure to keep unit
    // tests focused on the arg shape (and to guard against a regression back to the old broken
    // "youtube:po_token=web.gvs+TOKEN" manual-injection path).
    public static List<string> BuildBgutilPluginArgs(string pluginDir, int potPort)
    {
        if (potPort <= 0 || string.IsNullOrWhiteSpace(pluginDir)) return new List<string>();
        return new List<string>
        {
            "--plugin-dirs", pluginDir,
            // player_js_variant=main avoids an 'origin' TypeError in the TV player variant that
            // yt-dlp otherwise picks.
            "--extractor-args", "youtube:player_js_variant=main",
            // bgutil lives under its own extractor scope (youtubepot-bgutilhttp) — it MUST be a
            // separate --extractor-args flag. Packing it into the youtube: string after a semicolon
            // makes yt-dlp interpret the whole "youtubepot-bgutilhttp:base_url=..." as a youtube
            // key, so the plugin never sees our base_url and silently falls back to the hardcoded
            // 127.0.0.1:4416 default — which is not what we're listening on.
            "--extractor-args", "youtubepot-bgutilhttp:base_url=http://localhost:" + potPort
        };
    }

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
    // Pref=1080p → floor=720p ✓, pref=720p → floor=480p ✓, pref=480p → floor=320p.
    public static int ComputeQualityFloor(int preferredHeight) =>
        (int)(preferredHeight * 2.0 / 3.0);

    // A tier result is "good enough" if we don't know its height (trust-by-default) or it's ≥ floor.
    public static bool IsAcceptableQuality(int? resolvedHeight, int floorHeight) =>
        resolvedHeight == null || resolvedHeight >= floorHeight;

    // === WHY WE WRAP ===
    // VRChat enforces a trusted-URL list inside AVPro. Media URLs whose host does not match the
    // allowlist (e.g. *.youtube.com, youtu.be, vimeo.com, …) are silently rejected with
    // "[AVProVideo] Error: Loading failed. File not found, codec not supported, video resolution
    // too high or insufficient system resources." The relay wrap rewrites any URL as
    //   http://localhost.youtube.com:{port}/play?target=<base64>
    // which — via the hosts-file mapping `127.0.0.1 localhost.youtube.com` — routes to our local
    // relay while AVPro sees a trusted *.youtube.com host. This is the ONLY reason untrusted
    // cloud/proxy URLs (node1.whyknot.dev from tier2:cloud-whyknot, signed-URL CDNs, etc.) play at
    // all. Wrapping is the DEFAULT; do not "optimize" by skipping it for non-YouTube hosts — every
    // untrusted URL that reaches AVPro pristine will silently fail playback.
    //
    // The single narrow exception is the config-driven deny-list `AppConfig.NativeAvProUaHosts`,
    // which holds hosts that serve only to AVPro's UnityPlayer UA (VRChat "movie worlds" like
    // vr-m.net). Wrapping those breaks UA passthrough and the origin 403s us. Every addition to
    // that list must be backed by a log capture showing the host working pristine but failing
    // through the relay. See the feedback_relay_purpose memory for history.
    private bool RequiresNativeAvProUa(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        var denylist = _settings.Config.NativeAvProUaHosts;
        if (denylist == null || denylist.Count == 0) return false;
        foreach (var entry in denylist)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            string d = entry.Trim().ToLowerInvariant();
            if (host == d || host.EndsWith("." + d)) return true;
        }
        return false;
    }

    // VRChat's built-in AVPro trusted-URL allowlist (as of 2026-04-23). Hosts matching these
    // patterns play pristine — AVPro accepts them without any trust-list bypass. Hosts OFF this
    // list silently fail with "Loading failed" unless relay-wrapped.
    //
    // Source: in-game trust check shipped with VRChat. Keep synchronized with the
    // project_vrchat_trusted_url_list memory file (same table). Adding an entry is a one-way ticket
    // to skipping the relay wrap on that host, so only add after verifying VRChat trusts it.
    private static readonly string[] _vrchatTrustedHostPatterns = new[]
    {
        "vod-progressive.akamaized.net",
        "*.facebook.com", "*.fbcdn.net",
        "*.googlevideo.com",
        "*.hyperbeam.com", "*.hyperbeam.dev",
        "*.mixcloud.com",
        "*.nicovideo.jp",
        "soundcloud.com", "*.sndcdn.com",
        "*.topaz.chat",
        "*.twitch.tv", "*.ttvnw.net", "*.twitchcdn.net",
        "*.vrcdn.live", "*.vrcdn.video", "*.vrcdn.cloud",
        "*.vimeo.com",
        "*.youku.com",
        "*.youtube.com", "youtu.be",
    };

    public static bool IsVrchatTrustedHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        foreach (var pattern in _vrchatTrustedHostPatterns)
        {
            if (pattern.StartsWith("*."))
            {
                string suffix = pattern.Substring(1); // ".youtube.com"
                string bare = pattern.Substring(2);   // "youtube.com"
                if (host == bare || host.EndsWith(suffix)) return true;
            }
            else
            {
                if (host == pattern) return true;
            }
        }
        return false;
    }

    // Wrap a pristine resolved URL in the localhost.youtube.com relay URL so AVPro sees a trusted
    // host. Default path: wrap everything. Skipped only when:
    //   - skipRelayWrap=true (Share mode — user is copying a plain URL out of WKVRCProxy),
    //   - EnableRelayBypass config flag is off,
    //   - the hosts-file mapping isn't active (setup declined),
    //   - the host is in the `NativeAvProUaHosts` config deny-list (movie-world hosts).
    // `forceWrap` is retained for callers (browser-extract session replay) that want to override
    // an otherwise-skipped wrap, but with the default now being "wrap", it's mostly redundant.
    private string ApplyRelayWrap(string pristineUrl, bool skipRelayWrap, string correlationId, bool forceWrap = false)
    {
        if (skipRelayWrap)
            return pristineUrl;
        if (!_settings.Config.EnableRelayBypass)
        {
            _logger.Warning("[" + correlationId + "] Relay bypass is DISABLED in config — returning pristine URL. Untrusted hosts will likely fail VRChat's trusted-URL check.");
            return pristineUrl;
        }
        if (!_hostsManager.IsBypassActive())
        {
            _logger.Warning("[" + correlationId + "] Hosts-file bypass is not active — returning pristine URL. VRChat's AVPro will reject untrusted hosts. Run the hosts setup from Settings.");
            return pristineUrl;
        }
        if (!forceWrap && RequiresNativeAvProUa(pristineUrl))
        {
            string host = Uri.TryCreate(pristineUrl, UriKind.Absolute, out var u) ? u.Host : "<unparseable>";
            _logger.Info("[" + correlationId + "] Relay wrap skipped for " + host + " — host requires AVPro's native UA (NativeAvProUaHosts).");
            return pristineUrl;
        }
        if (!forceWrap && IsVrchatTrustedHost(pristineUrl))
        {
            string host = Uri.TryCreate(pristineUrl, UriKind.Absolute, out var u) ? u.Host : "<unparseable>";
            _logger.Debug("[" + correlationId + "] Relay wrap skipped for " + host + " — already on VRChat's trusted-URL list (pristine passthrough).");
            return pristineUrl;
        }
        try
        {
            int port = _relayPortManager.CurrentPort;
            if (port <= 0)
            {
                _logger.Warning("[" + correlationId + "] Relay bypass is enabled but relay port is 0 — wrapping skipped. Video will likely fail to play (untrusted host).");
                return pristineUrl;
            }
            string encodedUrl = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pristineUrl));
            string relayUrl = "http://localhost.youtube.com:" + port + "/play?target=" + WebUtility.UrlEncode(encodedUrl);
            _logger.Info("[" + correlationId + "] URL relay-wrapped on port " + port + ".");
            return relayUrl;
        }
        catch (Exception ex)
        {
            _logger.Warning("[" + correlationId + "] Failed to wrap URL for relay: " + ex.Message);
            return pristineUrl;
        }
    }

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
        // Clamp: tier4 is the "always return something" backstop. It CANNOT be disabled — the
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
                // runs through tier1→2→3, all fail, and the user waits 30+ seconds for an inevitable
                // tier4 fallback (see vr-m.net 32-second resolution at 13:54:45 in the 04-26 logs).
                string host = Uri.TryCreate(targetUrl, UriKind.Absolute, out var u) ? u.Host : "<unparseable>";
                _logger.Info("[" + ctx.CorrelationId + "] " + host + " is in NativeAvProUaHosts — short-circuiting to tier 4 passthrough (cascade would only waste time).");
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
                // when streamlink isn't installed or doesn't claim the host — without it, a
                // YouTube /live/ URL inherits the VOD fast-path memory and hangs on tier2.
                bool isLiveForMemory = isStreamlinkLive || StrategyMemory.LooksLikeLive(targetUrl);
                // Include the player in the memory key — AVPro and Unity need different formats,
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

                _logger.Info("[" + ctx.CorrelationId + "] Cascade: " + string.Join(" → ", cascade.Select(t => t.ToUpper())) +
                    (disabled.Count > 0 ? " (disabled: " + string.Join(", ", disabled) + ")" : "") +
                    " (quality floor " + floorH + "p)");

                // activeStrategy is the specific variant label written to StrategyMemory on success.
                // activeTier is the tier-group (backwards compat for UI / HistoryEntry.Tier).
                string activeStrategy = "";

                // Tier 0: Streamlink — live-stream fast-path. Skipped when a faster (tier1/2/3)
                // winner is already remembered. Streamlink does not report resolution, so the
                // quality heuristic treats it as unknown → accepted.
                if (isStreamlinkLive && !disabled.Contains("tier0"))
                {
                    bool tryStreamlink = rememberedGroup == null || rememberedGroup == "tier0";
                    if (tryStreamlink)
                    {
                        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Streamlink supports this URL — attempting resolution.");
                        var (slRes, slMs) = await TimedResolve(() => ResolveStreamlink(targetUrl, ctx));
                        if (slRes != null)
                        {
                            _logger.Info("[" + ctx.CorrelationId + "] [Tier 0] Streamlink success in " + slMs + "ms.");
                            winnerResult = slRes; activeTier = "tier0-streamlink"; activeStrategy = "tier0:streamlink-native";
                        }
                        else if (rememberedGroup == "tier0")
                        {
                            _logger.Warning("[" + ctx.CorrelationId + "] [StrategyMemory] Remembered tier0 strategy failed — demoting.");
                            RecordStrategyFailure(memKey, remembered!.StrategyName);
                            remembered = null; rememberedGroup = null;
                        }
                        else
                        {
                            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Streamlink returned no URL after " + slMs + "ms — cascading.");
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
                    // — it would run the tier's default recipe (ResolveTier1 etc.), which is a
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
                                    + "' failed (" + reason + ") in " + fastSw.ElapsedMilliseconds + "ms — demoting and cold-racing.");
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
                                + " — cold-racing instead.");
                            remembered = null; rememberedGroup = null;
                        }
                    }

                    // Cold-start race: no memory, Tier 1 is first, Tier 2 is also active. Race every
                    // applicable Tier 1 variant plus Tier 2 in parallel — concurrency capped. First past
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
                                    _logger.Warning("[" + ctx.CorrelationId + "] [Race] Per-host budget for " + hostForBudget + " exhausted — skipping '" + s.Name + "' (rate-limit guard).");
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
                                        _logger.Warning("[" + ctx.CorrelationId + "] [Race] '" + strat.Name + "' URL failed pre-flight probe — demoting and skipping.");
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
                                        // do NOT overwrite the winner — otherwise a second-place
                                        // strategy that happened to finish milliseconds later
                                        // would replace the true first-past-the-post result. This
                                        // is the "double winner" bug that previously caused a
                                        // demoted strategy's URL to replace cloud's on retry.
                                        _logger.Debug("[" + ctx.CorrelationId + "] [Race] '" + strat.Name + "' also succeeded in the same pass, but '" + activeStrategy + "' already won — discarding.");
                                        RecordStrategySuccess(memKey, strat.Name, res.Height);
                                        continue;
                                    }
                                    winnerResult = res; activeTier = strat.Group; activeStrategy = strat.Name;
                                    winnerForcesRelayWrap = strat.ForceRelayWrap;
                                    _logger.Info("[" + ctx.CorrelationId + "] [Race] '" + strat.Name + "' cleared floor in " + raceSw.ElapsedMilliseconds + "ms — winner.");
                                    try { raceCts.Cancel(); } catch { }

                                    // Sweep already-completed pending tasks so StrategyMemory sees their outcomes.
                                    // Without this, a loser that finished BEFORE the winner (e.g. tier1:ipv6 dying
                                    // fast on a getaddrinfo failure) is silently dropped — the demote threshold
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
                                            _logger.Debug("[" + ctx.CorrelationId + "] [Race] '" + sweptStrat.Name + "' also succeeded but '" + activeStrategy + "' already won — discarding.");
                                            RecordStrategySuccess(memKey, sweptStrat.Name, sweptRes.Height);
                                        }
                                    }
                                    break; // stop draining — any still-pending tasks are mid-cancellation.
                                }
                                else
                                {
                                    _logger.Info("[" + ctx.CorrelationId + "] [Race] '" + strat.Name + "' returned " + res.Height + "p < " + floorH + "p floor — keeping as fallback.");
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
                                        // All remaining were budget-skipped — nothing left to wait for.
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
                            _logger.Debug("[" + ctx.CorrelationId + "] [Race] No winner — continuing cascade at index " + cascadeStart + ".");
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
                        // other tier-2 strategy to try — so this tier is effectively muted.
                        if (IsStrategyDisabled(tierStrategy, tier, disabled))
                        {
                            _logger.Debug("[" + ctx.CorrelationId + "] [" + tier + "] Skipped — '" + tierStrategy + "' is disabled by user config.");
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
                            // returned — producing false "successes" that poisoned StrategyMemory.
                            // Skip if config opts out, or if the probe has already run (Tier 1).
                            if (_settings.Config.EnablePreflightProbe && tier != "tier1")
                            {
                                bool reachable = await CheckUrlReachable(tierResult.Url, ctx);
                                if (!reachable)
                                {
                                    _logger.Warning("[" + ctx.CorrelationId + "] [" + tier + "] URL returned but pre-flight probe rejected it — demoting '" + tierStrategy + "' and cascading.");
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

                            _logger.Info("[" + ctx.CorrelationId + "] [" + tier + "] returned " + tierResult.Height + "p < " + floorH + "p floor — cascading to next tier for better quality.");
                            if (bestSoFar == null || (tierResult.Height ?? 0) > (bestSoFar.Height ?? 0))
                            {
                                // Record the full strategy name (e.g. "tier2:cloud-whyknot"), not just "tier2".
                                // The best-of fallback at the end of the cascade feeds this name into StrategyMemory —
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
                            _logger.Warning("[" + ctx.CorrelationId + "] [StrategyMemory] Remembered group '" + rememberedGroup + "' failed — retrying full cascade.");
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
                // the last-resort "something is better than nothing" contract — AVPro may still
                // reject it, but the program never returns null/empty.
                if (winnerResult == null)
                {
                    _logger.Warning("[" + ctx.CorrelationId + "] All active tiers exhausted — falling back to original URL (passthrough). Video may not play correctly.");
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

                    // Anonymous reporting hook. Fire-and-forget — the user doesn't wait on the
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
        // a fresh cascade attempt next time — we don't want to "remember" a failed cascade.
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
            // Pass the pre-wrap upstream URL too — the relay-abort detector reports that, not
            // the wrapped /play?target=… URL the player actually fetched.
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
    private async Task<YtDlpResult?> AttemptTier1(string url, string player, RequestContext ctx)
    {
        var (res, ms) = await TimedResolve(() => ResolveTier1(url, player, ctx));
        if (res == null)
        {
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] yt-dlp returned no URL after " + ms + "ms — check stderr above for cause.");
            return null;
        }
        if (!await CheckUrlReachable(res.Url, ctx))
        {
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] URL resolved in " + ms + "ms but failed reachability check — cascading to next tier.");
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
    // against the same host. Match yt-dlp maintainer guidance of ≤2–3 concurrent requests per
    // origin. Cloud (tier 2) is exempt — it hits whyknot.dev from a different IP.
    private static string HostFromUrl(string url)
    {
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return ""; }
    }

    // Best-effort reservation of a slot in the rolling window. Returns true if the spawn is
    // allowed and records the timestamp; false if over budget. Old entries are pruned on each call.
    private bool TryConsumeHostBudget(string host)
    {
        if (string.IsNullOrEmpty(host)) return true;
        int budget = Math.Max(1, _settings.Config.PerHostRequestBudget);
        int windowSec = Math.Max(1, _settings.Config.PerHostRequestWindowSeconds);
        var cutoff = DateTime.UtcNow.AddSeconds(-windowSec);
        var queue = _hostRequestLog.GetOrAdd(host, _ => new Queue<DateTime>());
        lock (_hostRequestLogLock)
        {
            while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
            if (queue.Count >= budget) return false;
            queue.Enqueue(DateTime.UtcNow);
            return true;
        }
    }

    // How long a playback-failure demotion keeps a strategy at the tail of the race. After this
    // window, demoted strategies re-enter their normal priority position and get another chance.
    // Playback failures are often transient (IP rate flag clears in minutes, CDN rotates keys,
    // user switches networks) — a permanent ban would be too aggressive.
    private static readonly TimeSpan DemotedStrategyCooldown = TimeSpan.FromMinutes(30);

    // A strategy is "currently demoted" if its last failure crossed the demote threshold AND that
    // failure happened within the cooldown window. Matches StrategyMemory's own definition used
    // in the fast-path (GetPreferred), so cold race and fast path agree on who's benched.
    private static bool IsStrategyCurrentlyDemoted(StrategyMemoryEntry? entry)
    {
        if (entry == null) return false;
        int threshold = StrategyMemory.DemoteThresholdFor(entry.LastFailureKind);
        if (entry.ConsecutiveFailures < threshold) return false;
        if (entry.LastFailure is DateTime lf && DateTime.UtcNow - lf > DemotedStrategyCooldown)
            return false;
        return true;
    }

    // Reorder a strategy catalog by:
    //   (1) healthy-vs-demoted bucket — strategies with a recent PlaybackFailed/Blocked403/etc go
    //       to the tail REGARDLESS of their priority position, so a known-bad strategy can't
    //       keep winning wave 1 just because the user put it at the top. Without this, the cold
    //       race would re-fire the same failing strategy on every retry, which is exactly what
    //       happened with tier1:yt-combo returning AVPro-rejected googlevideo URLs.
    //   (2) user's StrategyPriority list (explicit) — within each bucket.
    //   (3) memory net-score for this host — tiebreaker when two strategies share priority.
    //   (4) built-in priority number — final fallback.
    // Demotion is time-bound (see DemotedStrategyCooldown): after the cooldown, demoted
    // strategies rejoin the healthy pool. Prevents a one-off transient from permanently
    // benching a normally-working strategy.
    private List<ResolveStrategy> OrderStrategiesForRace(List<ResolveStrategy> catalog, string memKey)
    {
        var priorityList = _settings.Config.StrategyPriority ?? new List<string>();
        var priorityIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < priorityList.Count; i++)
            if (!priorityIndex.ContainsKey(priorityList[i]))
                priorityIndex[priorityList[i]] = i;

        var memoryEntries = string.IsNullOrEmpty(memKey)
            ? new List<StrategyMemoryEntry>()
            : _strategyMemory.GetAll(memKey).ToList();
        var memoryByName = memoryEntries.ToDictionary(e => e.StrategyName, e => e, StringComparer.OrdinalIgnoreCase);
        var netScore = memoryEntries.ToDictionary(e => e.StrategyName, e => e.NetScore, StringComparer.OrdinalIgnoreCase);

        return catalog
            .OrderBy(s => IsStrategyCurrentlyDemoted(memoryByName.GetValueOrDefault(s.Name)) ? 1 : 0)
            .ThenBy(s => priorityIndex.TryGetValue(s.Name, out var pi) ? pi : int.MaxValue)
            .ThenByDescending(s => netScore.TryGetValue(s.Name, out var score) ? score : 0)
            .ThenBy(s => s.Priority)
            .ToList();
    }

    // The disabled list accepts two shapes:
    //   - tier group names:    "tier0", "tier1", "tier2", "tier3", "tier4"  (disables the whole tier)
    //   - specific strategies: "tier1:browser-extract", "tier2:cloud-whyknot", ...  (disables one variant)
    // Both live in the same flat list for simplicity. A strategy is skipped iff its full name
    // OR its tier group is in the list. Users who only want to temporarily turn off a buggy
    // strategy (e.g. browser-extract on YouTube) can untick just that entry in Settings and keep
    // the rest of tier 1 active.
    private static bool IsStrategyDisabled(string strategyName, string group, List<string> disabled)
    {
        if (disabled.Contains(group)) return true;
        foreach (var entry in disabled)
        {
            if (string.Equals(entry, strategyName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private List<ResolveStrategy> BuildColdRaceStrategies(string url, string player, string[] originalArgs, List<string> disabled)
    {
        var list = new List<ResolveStrategy>();
        bool isYouTubeHost = url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                          || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
        string? videoId = isYouTubeHost ? ExtractYouTubeVideoId(url) : null;

        if (!disabled.Contains("tier1"))
        {
            // YouTube mega-combo. Replaces the 6+ individual player_client strategies we used to
            // fire as separate subprocesses. yt-dlp tries each client in `YouTubeComboClientOrder`
            // sequentially within ONE process, stopping at the first usable format. In the common
            // case this means:
            //   * 1 subprocess per YouTube play (not 10+)
            //   * 1 outbound request to YouTube (yt-dlp stops after first success)
            //   * N requests only in the worst case where every preceding client is patched
            // The client order is user-configurable (SettingsManager seeds from
            // YouTubeComboClientOrderDefault). Power users can surface a known-working client to
            // position 0 to skip internal retries entirely.
            if (isYouTubeHost)
            {
                var comboClients = _settings.Config.YouTubeComboClientOrder;
                if (comboClients == null || comboClients.Count == 0)
                    comboClients = new List<string>(StrategyDefaults.YouTubeComboClientOrderDefault);
                string comboClientList = string.Join(",", comboClients);
                string comboExtractorArgs = comboClientList + ";player_js_variant=main";

                list.Add(new ResolveStrategy("tier1:yt-combo", "tier1", 5, true,
                    sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                        injectPot: true, injectImpersonate: false,
                        userAgent: null, referer: null,
                        videoId: videoId, variantLabel: "yt-combo",
                        playerClient: comboExtractorArgs)));
            }

            // Default variant: auto PO-token + auto impersonate. Primary for non-YouTube hosts;
            // YouTube URLs use tier1:yt-combo above instead, but this stays in the catalog as a
            // last-resort tier-1 fallback.
            list.Add(new ResolveStrategy("tier1:default", "tier1", 10, true,
                sctx => ResolveTier1(sctx.Url, sctx.Player, sctx.RequestContext)));

            // IPv6-forced variant. If YouTube (or any origin) is rate-limiting your IPv4 address
            // but the flag doesn't apply to IPv6 — common, since CGNAT / residential bot flags
            // typically target v4 pools — forcing yt-dlp to egress via v6 routes around the
            // block at the IP layer without any fingerprint changes. Silent no-op on networks
            // that don't have v6 connectivity (yt-dlp falls back, call still fails cleanly).
            list.Add(new ResolveStrategy("tier1:ipv6", "tier1", 15, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: isYouTubeHost, injectImpersonate: false,
                    userAgent: null, referer: null,
                    videoId: videoId, variantLabel: "ipv6",
                    playerClient: isYouTubeHost ? string.Join(",", _settings.Config.YouTubeComboClientOrder ?? new List<string>(StrategyDefaults.YouTubeComboClientOrderDefault)) + ";player_js_variant=main" : null,
                    forceIpv6: true)));

            // VRChat UA: for movie-world hosts that allowlist UnityPlayer. Tier 1 sees a successful
            // generic-extractor probe instead of the 403 it gets with the default UA.
            list.Add(new ResolveStrategy("tier1:vrchat-ua", "tier1", 20, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: false, injectImpersonate: false,
                    userAgent: VrchatAvProUserAgent, referer: VrchatReferer,
                    videoId: videoId, variantLabel: "vrchat-ua")));

            // curl-impersonate without PO token: for sites where the PO-token request itself flags us.
            list.Add(new ResolveStrategy("tier1:impersonate-only", "tier1", 30, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: false, injectImpersonate: true,
                    userAgent: null, referer: null,
                    videoId: videoId, variantLabel: "impersonate-only")));

            // Plain yt-dlp: last-resort bypass for hosts that work without any extras.
            list.Add(new ResolveStrategy("tier1:plain", "tier1", 40, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: false, injectImpersonate: false,
                    userAgent: null, referer: null,
                    videoId: videoId, variantLabel: "plain")));

            // NOTE: Per-player_client YouTube strategies (po-only, ios-music, tv-embedded,
            // android-vr, web-safari, mweb) were REMOVED in v2 defaults. They're now rolled into
            // tier1:yt-combo above as one subprocess with all clients in YouTubeComboClientOrder.
            // yt-dlp tries each client in order internally, stops at first success, so one
            // YouTube play = 1 request to YouTube instead of 10. To change which client gets tried
            // first, edit AppConfig.YouTubeComboClientOrder — don't re-introduce per-client
            // strategies.
        }

        if (!disabled.Contains("tier2"))
        {
            list.Add(new ResolveStrategy("tier2:cloud-whyknot", "tier2", 10, false,
                sctx => AttemptTier2(sctx.Url, sctx.Player, sctx.RequestContext)));
        }

        // WARP variants: same yt-dlp recipes but egress via Cloudflare WARP (SOCKS5 loopback).
        // Useful for origins that geo-block or IP-flag the user's home ISP — WARP presents a
        // Cloudflare edge IP that many CDNs trust by default. Fires last in the race (priority
        // 90/95) since most direct requests succeed; WARP is the "try a different network" retry.
        // Registered whenever WarpService is available and listed in the default priority list at
        // the tail; users can disable a specific variant via the Settings Strategy Panel.
        // EnsureRunningAsync lazily starts wireproxy on first run, so listing them costs nothing
        // when they never get raced.
        //
        // When MaskIp is on, every other tier-1 variant already routes through WARP (see the
        // effectiveUseWarp branch in RunTier1Attempt). The warp+ entries become byte-identical
        // duplicates of `tier1:default` and `tier1:vrchat-ua` in that mode, so we skip them to
        // avoid noise in the strategy panel and StrategyMemory.
        bool maskIp = _settings.Config.MaskIp;
        if (!disabled.Contains("tier1") && _warp != null && !maskIp)
        {
            list.Add(new ResolveStrategy("tier1:warp+default", "tier1", 90, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: false, injectImpersonate: _curlClient?.IsAvailable == true,
                    userAgent: null, referer: null,
                    videoId: videoId, variantLabel: "warp+default", playerClient: null, useWarp: true)));

            list.Add(new ResolveStrategy("tier1:warp+vrchat-ua", "tier1", 95, true,
                sctx => RunTier1Attempt(sctx.Url, sctx.Player, sctx.RequestContext,
                    injectPot: false, injectImpersonate: false,
                    userAgent: VrchatAvProUserAgent, referer: VrchatReferer,
                    videoId: videoId, variantLabel: "warp+vrchat-ua", playerClient: null, useWarp: true)));
        }

        // Browser-extract: last-resort bypass. A real headless browser visits the page, captures
        // the first media URL it sees, and (if the origin is gated) the captured session headers
        // are stashed in BrowserSessionCache so the relay replays them on AVPro's requests. Costs
        // a browser page load (~3–8s) so it fires last in the race — earlier strategies usually win.
        // ForceRelayWrap=true: even non-YouTube URLs need to flow through the relay so captured
        // cookies/headers reach AVPro's subsequent requests. Subprocess=false: runs in-proc via
        // PuppeteerSharp (the browser is itself a subprocess but not counted against the semaphore).
        if (!disabled.Contains("tier1") && _browserExtractor != null && _settings.Config.EnableBrowserExtract)
        {
            list.Add(new ResolveStrategy("tier1:browser-extract", "tier1", 80, false,
                sctx => RunBrowserExtract(sctx.Url, sctx.RequestContext, sctx.Cancellation),
                ForceRelayWrap: true));
        }

        // Filter out any specific strategy the user has disabled by full name. The tier-group
        // Contains checks above already handled whole-tier disables, but a user may want to keep
        // tier 1 active while muting one buggy variant (e.g. "tier1:browser-extract" while that
        // path is producing decoy URLs for a host).
        int before = list.Count;
        list = list.Where(s => !IsStrategyDisabled(s.Name, s.Group, disabled)).ToList();
        if (list.Count < before)
        {
            _logger.Debug("[ColdRace] Filtered out " + (before - list.Count) + " strategy variant(s) by user config.");
        }
        return list;
    }

    // Strategy runner used by the cold-race dispatcher. Honours the shared semaphore and the
    // race-wide cancellation token. Returns (strategy, null) if cancelled or the executor failed.
    private async Task<(ResolveStrategy Strategy, YtDlpResult? Result)> RunStrategySlot(
        ResolveStrategy s, StrategyRunContext sctx,
        System.Threading.SemaphoreSlim sem, System.Threading.CancellationToken ct)
    {
        try { await sem.WaitAsync(ct); }
        catch (OperationCanceledException) { return (s, null); }
        try
        {
            if (ct.IsCancellationRequested) return (s, null);
            var r = await s.Executor(sctx);
            return (s, r);
        }
        catch (Exception ex)
        {
            _logger.Debug("[" + sctx.RequestContext.CorrelationId + "] Strategy '" + s.Name + "' threw: " + ex.Message);
            return (s, null);
        }
        finally { try { sem.Release(); } catch { } }
    }

    private async Task<YtDlpResult?> ResolveTier1(string url, string player, RequestContext ctx)
    {
        _logger.Debug("[" + ctx.CorrelationId + "] [Tier 1] Attempting native yt-dlp resolution...");

        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");
        string host = ExtractHost(url);
        string? videoId = isYouTube ? ExtractYouTubeVideoId(url) : null;

        // Decide whether to fetch a PO token up front. YouTube doesn't require PO on every request —
        // it flips into bot-detection mode domain-wide for a window of ~30 min. The fast-path (no PO)
        // completes in 2-3s when YouTube is happy; PO token fetch adds 5-15s. So: only pay the PO cost
        // when we've recently seen a bot-check for this host.
        bool needsPot = isYouTube && DomainRequiresPot(host);
        var result = await RunTier1Attempt(url, player, ctx, injectPot: needsPot, videoId);

        // Fast-path failure mode: bot-check stderr even though we didn't send a PO token. Flag the
        // domain so the next request uses PO upfront. Don't retry in-call — the cascade falls through
        // to Tier 2 for this request; next Tier 1 call will take the PO path and likely succeed.
        if (result == null && !needsPot && isYouTube && IsBotDetectionStderr(_lastTier1Stderr))
        {
            MarkDomainRequiresPot(host);
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] YouTube bot detection triggered on fast-path for '" + host + "' — flagging domain for PO token for " + DomainRequiresPotTtl.TotalMinutes + " min.");
        }
        // PO-path failure: PO token was injected but bot-check still fired. Refresh the flag so we keep
        // using PO, and log loudly — this usually means the bgutil sidecar's token is stale.
        else if (result == null && needsPot && isYouTube && IsBotDetectionStderr(_lastTier1Stderr))
        {
            MarkDomainRequiresPot(host);
            _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1] YouTube bot detection triggered EVEN WITH PO token for '" + host + "' — check bgutil sidecar health.");
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
    // hosts when the browser session is required.
    private async Task<YtDlpResult?> RunBrowserExtract(string url, RequestContext ctx, CancellationToken ct)
    {
        if (_browserExtractor == null)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [browser-extract] Service not wired — strategy skipped.");
            return null;
        }
        if (!_settings.Config.EnableBrowserExtract)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [browser-extract] Disabled by config (EnableBrowserExtract=false).");
            return null;
        }

        // Deadline: 25s gives the browser enough time to load and intercept a first manifest while
        // still letting faster strategies win the race. Site load typically lands in 3–8s.
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
    // Different clients return different format sets and have different bot-detection profiles —
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
        // forceIpv6 wins over ForceIPv4 when the ipv6 strategy is explicitly selected — the whole
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
        // this specific yt-dlp subprocess is affected — nothing else on the host routes through WARP.
        //
        // Two ways to opt in:
        //   - useWarp=true        — strategy-level (the warp+ variants pass this).
        //   - Config.MaskIp=true  — global; every tier-1 yt-dlp call routes through WARP regardless
        //                           of which variant is firing.
        //
        // EnsureRunningAsync lazily starts wireproxy on first call (subsequent calls are O(1)). If
        // WARP genuinely can't start (binaries missing, port collision, wgcf failure), the strategy
        // fails outright rather than silently falling back to direct — otherwise a Mask-IP user
        // would think their IP is masked while it's actually leaking, and the cold-race winner
        // would look like "warp+default" while in reality doing exactly what tier1:default would.
        bool effectiveUseWarp = useWarp || _settings.Config.MaskIp;
        if (effectiveUseWarp)
        {
            if (_warp == null || !await _warp.EnsureRunningAsync())
            {
                string reason = _settings.Config.MaskIp && !useWarp
                    ? "Mask IP is on but WARP is unavailable (" + (_warp?.StatusDetail ?? "service not registered") + ") — refusing to leak real IP. Turn Mask IP off in Settings if WARP isn't usable on this machine."
                    : "WARP unavailable (" + (_warp?.StatusDetail ?? "service not registered") + ") — strategy aborted. Disable warp+ strategies in Settings if WARP isn't usable on this machine.";
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
                _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] PotProviderService not ready — skipping PO hookup.");
            }
            else if (!Directory.Exists(Path.Combine(pluginDir, "bgutil-ytdlp-pot-provider", "yt_dlp_plugins")))
            {
                _logger.Warning("[" + ctx.CorrelationId + "] [Tier 1:" + variantLabel + "] bgutil plugin dir missing at '" + pluginDir + "' — yt-dlp will run without PO support.");
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
            // selector ((mp4/best)[protocol^=http]) — copying the one yt-dlp knows Unity can
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
        // Required for CDN URLs protected by Cloudflare anti-bot (e.g. imvrcdn.com) — without this
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
    // Returns the video ID for watch/short/youtu.be URLs, or a channel/handle identifier for live channel URLs.
    // Channel live patterns: /channel/UCxxx/live, /c/Name/live, /user/Name/live, /@handle/live.
    private static string? ExtractYouTubeVideoId(string url)
    {
        try
        {
            var uri = new Uri(url);
            string path = uri.AbsolutePath;

            // Standard watch URL: youtube.com/watch?v=ID
            if (path == "/watch")
            {
                foreach (string part in uri.Query.TrimStart('?').Split('&'))
                {
                    if (part.StartsWith("v=")) return part.Substring(2);
                }
            }

            // Short URL: youtu.be/ID
            if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
                return path.TrimStart('/').Split('?')[0];

            // Shorts: /shorts/ID
            if (path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
                return path.Substring("/shorts/".Length).Split('/')[0].Split('?')[0];

            // Channel live streams — return a stable identifier for the PO token cache key
            // /channel/UCxxx/live  →  "channel:UCxxx"
            if (path.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring("/channel/".Length).Split('/')[0];
                if (!string.IsNullOrEmpty(segment)) return "channel:" + segment;
            }

            // /c/Name/live  →  "c:Name"
            if (path.StartsWith("/c/", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring("/c/".Length).Split('/')[0];
                if (!string.IsNullOrEmpty(segment)) return "c:" + segment;
            }

            // /user/Name/live  →  "user:Name"
            if (path.StartsWith("/user/", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring("/user/".Length).Split('/')[0];
                if (!string.IsNullOrEmpty(segment)) return "user:" + segment;
            }

            // /@handle/live  →  "@handle"
            if (path.StartsWith("/@", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring(1).Split('/')[0]; // keeps the @
                if (!string.IsNullOrEmpty(segment)) return segment;
            }
        }
        catch { }
        return null;
    }

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
        // Mask IP applies here too — tier 3 is the last-resort fallback, and we don't want it
        // leaking the real IP after the user opted into IP masking. Same loud-fail behavior as
        // tier 1: if WARP can't start, abort rather than silently going direct.
        if (_settings.Config.MaskIp)
        {
            if (_warp == null || !await _warp.EnsureRunningAsync())
            {
                _logger.Warning("[" + ctx.CorrelationId + "] [Tier 3:" + variantLabel + "] Mask IP is on but WARP is unavailable (" + (_warp?.StatusDetail ?? "service not registered") + ") — refusing to leak real IP through yt-dlp-og.");
                return null;
            }
            args.Add("--proxy");
            args.Add(_warp.SocksProxyUrl);
        }
        var (result, _) = await RunYtDlp("yt-dlp-og.exe", args, ctx);
        return result;
    }

    // Asks Streamlink whether it has a plugin that handles the given URL.
    // Uses `streamlink --can-handle-url <url>` which is a local plugin registry check —
    // no network call, completes in <500ms. Exit code 0 means Streamlink supports the URL;
    // non-zero means it doesn't. This is the authoritative gate for Tier 0: no hardcoded
    // domain lists, no URL pattern matching — Streamlink's own registry decides.
    //
    // Results are cached per-host to avoid paying ~500ms on every resolve for the same unsupported
    // domain (e.g. vr-m.net). Plugin list only changes across Streamlink upgrades, so 24h/7d TTLs
    // are plenty.
    private async Task<bool> StreamlinkCanHandleUrlAsync(string url, RequestContext ctx)
    {
        string path = GetBinaryPath("streamlink.exe");
        if (!File.Exists(path)) return false; // Not installed — skip silently

        string host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : url;
        if (_streamlinkCapabilityCache.TryGetValue(host, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Streamlink capability cache hit for " + host + " → " + cached.CanHandle + ".");
            if (!cached.CanHandle)
                _logger.Info("[" + ctx.CorrelationId + "] [Tier 0] Streamlink has no plugin for " + host + " — skipping (cached).");
            return cached.CanHandle;
        }
        bool result = await StreamlinkCanHandleUrlUncachedAsync(url, path, ctx);
        var ttl = result ? StreamlinkCacheTtlPositive : StreamlinkCacheTtlNegative;
        _streamlinkCapabilityCache[host] = (result, DateTime.UtcNow.Add(ttl));
        if (!result)
            _logger.Info("[" + ctx.CorrelationId + "] [Tier 0] Streamlink has no plugin for " + host + " — skipping.");
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

            // Drain stdout/stderr to prevent buffer deadlock — exit code is all we need.
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
                _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] --can-handle-url timed out — skipping Streamlink.");
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
        // Default off — ads pass through and play, no pause.
        if (_settings.Config.StreamlinkDisableTwitchAds && url.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--twitch-disable-ads");
            _logger.Debug("[" + ctx.CorrelationId + "] [Tier 0] Twitch ad filter ON — playback will stall during ad breaks.");
        }
        args.Add(url);
        args.Add("best");
        var (result, _) = await RunYtDlp("streamlink.exe", args, ctx, timeoutMs: 9000);
        return result;
    }

    // yt-dlp-og.exe lives in the VRChat Tools folder (created by PatcherService as a backup).
    // streamlink.exe lives in tools/streamlink/bin/ (portable zip layout) or tools/streamlink/.
    // All other binaries (yt-dlp.exe, redirector.exe) live in dist/tools/.
    private string GetBinaryPath(string binary)
    {
        if (binary == "streamlink.exe")
        {
            string slBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "streamlink");
            string slBin = Path.Combine(slBase, "bin", binary);
            if (File.Exists(slBin)) return slBin;
            return Path.Combine(slBase, binary);
        }
        if (binary == "yt-dlp-og.exe")
        {
            string? toolsDir = _patcher.VrcToolsDir;
            if (!string.IsNullOrEmpty(toolsDir))
            {
                string vrcPath = Path.Combine(toolsDir, binary);
                if (File.Exists(vrcPath)) return vrcPath;
            }
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", binary);
    }

    private async Task<(YtDlpResult? Result, string Stderr)> RunYtDlp(string binary, List<string> args, RequestContext ctx, int timeoutMs = 15000)
    {
        string path = GetBinaryPath(binary);
        if (!File.Exists(path))
        {
            _logger.Error("[" + ctx.CorrelationId + "] " + binary + " not found at: " + path);
            return (null, "");
        }

        // Sanitize args for logging — mask PO token value (it's long and security-sensitive)
        string loggableArgs = string.Join(" ", args.Select(a => a.StartsWith("youtube:po_token=") ? "youtube:po_token=[REDACTED]" : a));
        _logger.Debug("[" + ctx.CorrelationId + "] Executing: " + binary + " " + loggableArgs);

        try
        {
            var stdoutLines = new List<string>();
            var stdoutLock = new object();
            var urlSeenTcs = new TaskCompletionSource<bool>();

            using var process = new Process();
            process.StartInfo.FileName = path;
            process.StartInfo.Arguments = string.Join(" ", args.Select(a => "\"" + a + "\""));
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.OutputDataReceived += (s, e) => {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                string line = e.Data.Trim();
                lock (stdoutLock) stdoutLines.Add(line);
                // Signal early when a URL line appears, but don't complete — we still need the meta line.
                if (line.StartsWith("url:") || line.StartsWith("http"))
                    urlSeenTcs.TrySetResult(true);
            };

            // Capture stderr so errors from yt-dlp are visible in the log instead of silently discarded.
            var stderrLines = new StringBuilder();
            process.ErrorDataReceived += (s, e) => {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    stderrLines.AppendLine(e.Data.Trim());
            };

            process.Start();
            ProcessGuard.Register(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exitTcs = new TaskCompletionSource<bool>();
            _ = Task.Run(() => {
                try { process.WaitForExit(); }
                catch (ObjectDisposedException) { /* process disposed on timeout path — expected */ }
                catch (InvalidOperationException) { /* process never started or already cleaned up */ }
                exitTcs.TrySetResult(true);
            });

            var timeoutTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(exitTcs.Task, timeoutTask);

            // Log any stderr output regardless of whether it timed out or resolved
            string stderrOutput = stderrLines.ToString().Trim();
            if (!string.IsNullOrEmpty(stderrOutput))
                _logger.Warning("[" + ctx.CorrelationId + "] [" + binary + "] stderr: " + stderrOutput);

            if (completed == timeoutTask)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] " + binary + " timed out after " + (timeoutMs / 1000) + "s.");
                _eventBus?.PublishError("ResolutionEngine", new ErrorContext {
                    Category = ErrorCategory.ChildProcess,
                    Code = ErrorCodes.YTDLP_TIMEOUT,
                    Summary = binary + " timed out after " + (timeoutMs / 1000) + " seconds",
                    Detail = "The process did not produce a URL within the timeout window",
                    ActionHint = "The video source may be slow to respond. Try again or switch to a different tier.",
                    IsRecoverable = true
                }, ctx.CorrelationId);
                try { process.Kill(); } catch { /* Process may have already exited */ }
                return (null, stderrOutput);
            }

            // Non-zero exit codes are almost always the reason yt-dlp returned no URL
            if (process.HasExited && process.ExitCode != 0)
                _logger.Warning("[" + ctx.CorrelationId + "] " + binary + " exited with non-zero code " + process.ExitCode + ".");

            List<string> linesSnapshot;
            lock (stdoutLock) linesSnapshot = new List<string>(stdoutLines);

            var parsed = ParseYtDlpOutput(linesSnapshot);
            if (parsed == null)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] [" + binary + "] Process exited without outputting a URL (check stderr above).");
                return (null, stderrOutput);
            }

            string shortUrl = parsed.Url.Length > 100 ? parsed.Url.Substring(0, 100) + "..." : parsed.Url;
            string metaSummary = parsed.Height.HasValue ? parsed.Height + "p " + (parsed.Vcodec ?? "?") : "(no metadata)";
            _logger.Debug("[" + ctx.CorrelationId + "] [" + binary + "] resolved: " + shortUrl + " [" + metaSummary + "]");
            return (parsed, stderrOutput);
        }
        catch (Exception ex)
        {
            _logger.Error("[" + ctx.CorrelationId + "] " + binary + " execution error: " + ex.Message, ex);
            _eventBus?.PublishError("ResolutionEngine", new ErrorContext {
                Category = ErrorCategory.ChildProcess,
                Code = ErrorCodes.YTDLP_EXECUTION_ERROR,
                Summary = binary + " failed to execute",
                Detail = ex.Message,
                ActionHint = "The binary may be corrupted or missing. Try reinstalling WKVRCProxy.",
                IsRecoverable = false
            }, ctx.CorrelationId);
            return (null, "");
        }
    }

    // Parses yt-dlp stdout. Expects either:
    //   url:<url>                                            (tier 1 with --print url:%(url)s)
    //   meta:<height>|<width>|<vcodec>|<format_id>|<protocol>  (tier 1 with --print meta:...)
    // or a plain first-line URL (yt-dlp-og, streamlink). `NA` or empty fields → null.
    public static YtDlpResult? ParseYtDlpOutput(List<string> lines)
    {
        string? url = null;
        int? height = null, width = null;
        string? vcodec = null, formatId = null, protocol = null;

        foreach (var line in lines)
        {
            if (url == null && line.StartsWith("url:"))
            {
                string rest = line.Substring(4).Trim();
                if (rest.StartsWith("http")) url = rest;
            }
            else if (url == null && line.StartsWith("http"))
            {
                url = line;
            }
            else if (line.StartsWith("meta:"))
            {
                var parts = line.Substring(5).Split('|');
                if (parts.Length >= 1) height = ParseNullableInt(parts[0]);
                if (parts.Length >= 2) width = ParseNullableInt(parts[1]);
                if (parts.Length >= 3) vcodec = NullIfEmpty(parts[2]);
                if (parts.Length >= 4) formatId = NullIfEmpty(parts[3]);
                if (parts.Length >= 5) protocol = NullIfEmpty(parts[4]);
            }
        }

        return url == null ? null : new YtDlpResult(url, height, width, vcodec, formatId, protocol);
    }

    private static int? ParseNullableInt(string s)
    {
        s = s.Trim();
        if (string.IsNullOrEmpty(s) || s == "NA" || s == "None") return null;
        return int.TryParse(s, out var v) ? v : null;
    }

    private static string? NullIfEmpty(string s)
    {
        s = s.Trim();
        return (string.IsNullOrEmpty(s) || s == "NA" || s == "None") ? null : s;
    }

}
