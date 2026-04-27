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
// Partial — playback-feedback wiring. The cascade is generous about what counts as "success":
// any tier that returns a URL wins. This partial closes the feedback loop by listening for the
// real signals (RelayServer's OnClientAbortedEarly when Unity drops a connection mid-stream,
// VrcLogMonitor's OnAvProLoadFailure when AVPro logs "Loading failed") and demoting the strategy
// that produced the bad URL. The recent-resolutions ring keys a returned URL back to the
// strategy + memKey + history-row that produced it so the demote can target the right entry.
[SupportedOSPlatform("windows")]
public partial class ResolutionEngine
{
    // --- moved from ResolutionEngine.cs (lines 106-117) ---
    // Short-term record of what each recent resolution handed back. Keyed by BOTH the outgoing
    // resolved URL (post-wrap) AND the original user URL â€” VRChat's AVPro "Opening" log line can
    // reference either depending on whether VRChat's own yt-dlp hook intercepted. When
    // VrcLogMonitor fires OnAvProLoadFailure with an URL, we look it up here to find the strategy
    // that produced it, then demote that strategy with PlaybackFailed (one-strike demote).
    //
    // HistoryEntryRef lets us flip the matching history row's PlaybackVerified flag on the
    // feedback signal â€” without it, the UI's Success column would still lie about dead URLs.
    private record RecentResolution(string StrategyName, string MemKey, string OriginalUrl, string ResolvedUrl, string? UpstreamUrl, DateTime CreatedAt, string CorrelationId, HistoryEntry? HistoryEntryRef);
    private readonly ConcurrentDictionary<string, RecentResolution> _recentByUrl = new();
    private static readonly TimeSpan RecentResolutionTtl = TimeSpan.FromSeconds(60);
    private const int RecentResolutionCap = 64;

    // --- moved from ResolutionEngine.cs (lines 127-130) ---
    // How long to wait before promoting a history entry from "pending" to "verified". AVPro
    // typically logs "Loading failed" within 1-3 seconds of Opening; 8s is comfortable headroom
    // without making the UI feel stuck.
    private static readonly TimeSpan PlaybackVerifyDelay = TimeSpan.FromSeconds(8);

    // --- moved from ResolutionEngine.cs (lines 167-186) ---
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
    // it as a Unity PlaybackFailed â€” demote the strategy that produced it, evict the cache, and
    // fire the same StrategyDemoted event the AVPro path uses. Mirrors VRChat's retry cadence:
    // Unity reopens a failing URL every 5-8s, so 3 aborts inside 30s is a reliable signal with
    // zero false positives on normal playback (normal playback never aborts short).
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _relayAbortLog = new();
    private readonly object _relayAbortLogLock = new();
    private static readonly TimeSpan RelayAbortWindow = TimeSpan.FromSeconds(30);
    private const int RelayAbortThreshold = 3;

    // --- moved from ResolutionEngine.cs (lines 188-219) ---
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

        // Threshold hit â€” the player has repeatedly rejected this URL's format. Reuse the AVPro
        // failure handler, which already knows how to: match the URL to a recent resolution,
        // demote the strategy with PlaybackFailed, evict resolve cache, publish the event, and
        // flip the history entry's PlaybackVerified flag.
        lock (_relayAbortLogLock)
        {
            _relayAbortLog.TryRemove(targetUrl, out _);
        }
        _logger.Warning("[Playback] [Relay] Unity/AVPro aborted " + abortCountInWindow + "Ã— within " + RelayAbortWindow.TotalSeconds + "s on " +
            (targetUrl.Length > 80 ? targetUrl.Substring(0, 80) + "..." : targetUrl) + " â€” treating as PlaybackFailed.");
        HandleAvProLoadFailure(targetUrl, now);
    }

    // --- moved from ResolutionEngine.cs (lines 221-268) ---
    private void HandleAvProLoadFailure(string failedUrl, DateTime observedAt)
    {
        if (string.IsNullOrWhiteSpace(failedUrl)) return;
        PruneRecentResolutions();
        if (!_recentByUrl.TryGetValue(failedUrl, out var recent))
        {
            _logger.Debug("[Playback] AVPro Loading failed for URL not in recent-resolutions ring (" +
                (failedUrl.Length > 80 ? failedUrl.Substring(0, 80) + "..." : failedUrl) + "). Ignoring â€” likely a URL WKVRCProxy did not resolve.");
            return;
        }
        if (observedAt - recent.CreatedAt > RecentResolutionTtl)
        {
            _logger.Debug("[Playback] AVPro failure for resolved URL older than TTL â€” not demoting.");
            return;
        }
        _logger.Warning("[Playback] [" + recent.CorrelationId + "] AVPro rejected resolved URL from '" + recent.StrategyName + "' on " + recent.MemKey + ". Demoting (PlaybackFailed) â€” next request will re-cascade.");
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

    // --- moved from ResolutionEngine.cs (lines 270-302) ---
    private void RecordRecentResolution(string originalUrl, string resolvedUrl, string strategyName, string memKey, string correlationId, HistoryEntry? historyEntry = null, string? upstreamUrl = null)
    {
        if (string.IsNullOrEmpty(strategyName) || string.IsNullOrEmpty(memKey)) return;
        var rec = new RecentResolution(strategyName, memKey, originalUrl, resolvedUrl, upstreamUrl, DateTime.UtcNow, correlationId, historyEntry);
        _recentByUrl[originalUrl] = rec;
        if (!string.Equals(resolvedUrl, originalUrl, StringComparison.Ordinal))
            _recentByUrl[resolvedUrl] = rec;
        // Relay-abort detector reports the *upstream* URL (decoded `target` param), not the
        // wrapped /play?target=â€¦ URL the player sees. Without this third key, threshold-hit
        // aborts on relay-wrapped strategies (tier2 cloud, etc.) miss the ring lookup and
        // never demote â€” the resolve cache then keeps serving the same dead URL.
        if (!string.IsNullOrEmpty(upstreamUrl)
            && !string.Equals(upstreamUrl, originalUrl, StringComparison.Ordinal)
            && !string.Equals(upstreamUrl, resolvedUrl, StringComparison.Ordinal))
            _recentByUrl[upstreamUrl] = rec;
        PruneRecentResolutions();

        // Schedule the optimistic "verified" promotion. If no AVPro failure arrives within the
        // delay, we promote to true â€” a.k.a. "no news is good news". A failure observed in the
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

    // --- moved from ResolutionEngine.cs (lines 304-320) ---
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

}
