using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.IPC;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;
// Partial — cold-race orchestration. When neither a remembered winner nor a fresh resolve cache
// hit is available, the engine fires a parallel race across the strategy catalog. This file owns
// the per-host rate-limit budget, the demote-cooldown logic, the strategy-ordering/filtering, the
// catalog assembly, and the per-slot runner. Tier execution itself lives in
// ResolutionEngine.Tiers.cs.
[SupportedOSPlatform("windows")]
public partial class ResolutionEngine
{
    // --- moved from ResolutionEngine.cs (lines 110-113) ---
    // count against the local-IP rate limit. Budget size + window are config-driven
    // (PerHostRequestBudget / PerHostRequestWindowSeconds).
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hostRequestLog = new();
    private readonly object _hostRequestLogLock = new();

    // --- moved from ResolutionEngine.cs (lines 1139-1158) ---
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
    // user switches networks) â€” a permanent ban would be too aggressive.

    // --- moved from ResolutionEngine.cs (lines 1159-1395) ---
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
    //   (1) healthy-vs-demoted bucket â€” strategies with a recent PlaybackFailed/Blocked403/etc go
    //       to the tail REGARDLESS of their priority position, so a known-bad strategy can't
    //       keep winning wave 1 just because the user put it at the top. Without this, the cold
    //       race would re-fire the same failing strategy on every retry, which is exactly what
    //       happened with tier1:yt-combo returning AVPro-rejected googlevideo URLs.
    //   (2) user's StrategyPriority list (explicit) â€” within each bucket.
    //   (3) memory net-score for this host â€” tiebreaker when two strategies share priority.
    //   (4) built-in priority number â€” final fallback.
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
            // but the flag doesn't apply to IPv6 â€” common, since CGNAT / residential bot flags
            // typically target v4 pools â€” forcing yt-dlp to egress via v6 routes around the
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
            // first, edit AppConfig.YouTubeComboClientOrder â€” don't re-introduce per-client
            // strategies.
        }

        if (!disabled.Contains("tier2"))
        {
            list.Add(new ResolveStrategy("tier2:cloud-whyknot", "tier2", 10, false,
                sctx => AttemptTier2(sctx.Url, sctx.Player, sctx.RequestContext)));
        }

        // WARP variants: same yt-dlp recipes but egress via Cloudflare WARP (SOCKS5 loopback).
        // Useful for origins that geo-block or IP-flag the user's home ISP â€” WARP presents a
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
        // a browser page load (~3â€“8s) so it fires last in the race â€” earlier strategies usually win.
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

    // hosts when the browser session is required.
    // Returns the video ID for watch/short/youtu.be URLs, or a channel/handle identifier for live channel URLs.

}
