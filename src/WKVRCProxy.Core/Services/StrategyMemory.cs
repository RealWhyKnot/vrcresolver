using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

// Per-host/per-strategy learning memory. Supersedes the old TierMemory (which stored one tier per
// host). StrategyMemory tracks every strategy we've ever tried for a host, with success and failure
// counts, so the dispatcher can rank and retry in priority order — and decay entries when a
// previously-working bypass starts failing (sites patch their detection and we need to adapt).
//
// Persisted to strategy_memory.json. On first run we migrate from tier_memory.json by expanding
// each legacy tier entry into the canonical strategy for that tier group.

// Failure classification. Lets demotion react differently to transient vs. terminal vs. block
// failures, and lets the dispatcher promote specific strategies in response (e.g. JsChallenge →
// try browser-extract next time).
public enum StrategyFailureKind
{
    Unknown = 0,
    NetworkError,   // DNS, connection refused, DNS-resolution, etc. Transient.
    Timeout,        // request or subprocess timed out. Transient.
    Blocked403,     // 401/403/429. Strong block signal, specific to this IP/fingerprint.
    NotFound404,    // 404/410/451. URL is gone; retrying won't help.
    JsChallenge,    // site served a JS/captcha challenge page instead of the expected media.
    LowQuality,     // resolved a URL but its height was below the acceptable floor.
    PlaybackFailed  // resolution returned a URL but AVPro/pre-flight probe rejected it (trust list, codec, unreachable).
}

public class StrategyMemoryEntry
{
    public string StrategyName { get; set; } = "";
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public DateTime LastSuccess { get; set; }
    public DateTime? LastFailure { get; set; }
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;

    // Consecutive failures since last success. Compared against DemoteThresholdFor(LastFailureKind)
    // — different failure kinds trigger demotion at different counts (404 demotes immediately, a
    // transient timeout takes 5). A success resets this to 0.
    public int ConsecutiveFailures { get; set; }

    // Height of the last successful resolution. 0 = unknown (treated as neutral by the ranker).
    public int LastResolvedHeight { get; set; }

    // Running mean of successful resolution heights. Used as a tiebreaker during ranking so that a
    // strategy that consistently returns 1080p outranks one that returns 360p at equal W/L.
    // Computed as an exponential moving average: new = 0.7 * old + 0.3 * observed (fast to converge
    // when a site upgrades formats, slow enough that a single odd sample doesn't dominate).
    public double AverageResolvedHeight { get; set; }

    // Reason for the most recent failure. Lets the dispatcher react — e.g. if any strategy for a
    // host has LastFailureKind==JsChallenge, promote browser-extract in the cold race.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StrategyFailureKind LastFailureKind { get; set; }

    public int NetScore => SuccessCount - FailureCount;
}

public class StrategyMemory
{
    private readonly Logger? _logger;
    private readonly string _path;

    // key: "host:streamType" (same shape as the old TierMemoryKey)
    private readonly ConcurrentDictionary<string, List<StrategyMemoryEntry>> _entries = new();
    private readonly object _saveLock = new();

    // Default demote threshold for failure kinds not in the switch (Unknown, etc).
    public const int DefaultConsecutiveFailureDemoteThreshold = 3;
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(30);

    // Asymmetric demote thresholds — a strategy stops being "preferred" once this many consecutive
    // failures of the given kind hit without an intervening success.
    //   404 → 1 (URL is gone; retrying doesn't help)
    //   PlaybackFailed → 1 (URL "resolved" but AVPro refused it — as terminal as 404 from the user's POV)
    //   403 → 2 (ban signal; give it one retry in case of transient IP reputation)
    //   JsChallenge → 2 (strategy can't pass the gate; browser-extract should take over)
    //   LowQuality → 3 (give the strategy a chance to roll a better format)
    //   Timeout / NetworkError → 5 (transient, let it retry)
    public static int DemoteThresholdFor(StrategyFailureKind kind) => kind switch
    {
        StrategyFailureKind.NotFound404 => 1,
        StrategyFailureKind.PlaybackFailed => 1,
        StrategyFailureKind.Blocked403 => 2,
        StrategyFailureKind.JsChallenge => 2,
        StrategyFailureKind.LowQuality => 3,
        StrategyFailureKind.Timeout => 5,
        StrategyFailureKind.NetworkError => 5,
        _ => DefaultConsecutiveFailureDemoteThreshold
    };

    public StrategyMemory(Logger? logger, string basePath)
    {
        _logger = logger;
        _path = Path.Combine(basePath, "strategy_memory.json");
    }

    public int EntryCount => _entries.Count;

    // Returns the entry the dispatcher should try first, or null to force a cold race.
    // Ranking order:
    //   1. NetScore (successes - failures)
    //   2. AverageResolvedHeight (tiebreaker: prefer the strategy that gets better quality)
    //   3. LastSuccess recency
    // Entries past their kind-specific demote threshold or older than StaleThreshold are excluded.
    public StrategyMemoryEntry? GetPreferred(string memKey)
    {
        if (!_entries.TryGetValue(memKey, out var list)) return null;
        var now = DateTime.UtcNow;
        StrategyMemoryEntry? best = null;
        lock (list)
        {
            foreach (var e in list)
            {
                if (e.ConsecutiveFailures >= DemoteThresholdFor(e.LastFailureKind)) continue;
                if (now - e.LastSuccess > StaleThreshold) continue;
                if (best == null || IsBetter(e, best)) best = e;
            }
        }
        return best;
    }

    private static bool IsBetter(StrategyMemoryEntry candidate, StrategyMemoryEntry current)
    {
        if (candidate.NetScore != current.NetScore) return candidate.NetScore > current.NetScore;
        if (Math.Abs(candidate.AverageResolvedHeight - current.AverageResolvedHeight) > 1e-3)
            return candidate.AverageResolvedHeight > current.AverageResolvedHeight;
        return candidate.LastSuccess > current.LastSuccess;
    }

    // For diagnostic/UI purposes: full ranked view. Does not filter out demoted/stale entries so
    // the user can see why something was demoted.
    public IReadOnlyList<StrategyMemoryEntry> GetAll(string memKey)
    {
        if (!_entries.TryGetValue(memKey, out var list)) return Array.Empty<StrategyMemoryEntry>();
        lock (list) { return list.OrderByDescending(e => e.NetScore).ThenByDescending(e => e.LastSuccess).ToList(); }
    }

    // Signals the dispatcher that this host's last failure was a JS challenge — any strategy that
    // needs a browser-rendered page should be promoted in the next cold race.
    public bool HostWantsBrowser(string memKey)
    {
        if (!_entries.TryGetValue(memKey, out var list)) return false;
        lock (list)
        {
            foreach (var e in list)
                if (e.LastFailureKind == StrategyFailureKind.JsChallenge && e.ConsecutiveFailures > 0)
                    return true;
        }
        return false;
    }

    public void RecordSuccess(string memKey, string strategyName) => RecordSuccess(memKey, strategyName, null);

    public void RecordSuccess(string memKey, string strategyName, int? resolvedHeight)
    {
        if (string.IsNullOrEmpty(memKey) || string.IsNullOrEmpty(strategyName)) return;
        // Tier 4 passthrough is a failure-mode fallback, not something we want the fast-path to pick.
        if (strategyName.StartsWith("tier4:", StringComparison.OrdinalIgnoreCase)) return;

        var now = DateTime.UtcNow;
        var list = _entries.GetOrAdd(memKey, _ => new List<StrategyMemoryEntry>());
        lock (list)
        {
            var entry = list.FirstOrDefault(e => string.Equals(e.StrategyName, strategyName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new StrategyMemoryEntry { StrategyName = strategyName, FirstSeen = now };
                list.Add(entry);
            }
            entry.SuccessCount++;
            if (entry.FailureCount > 0) entry.FailureCount = Math.Max(0, entry.FailureCount - 1);
            entry.ConsecutiveFailures = 0;
            entry.LastFailureKind = StrategyFailureKind.Unknown;
            entry.LastSuccess = now;
            if (resolvedHeight is int h && h > 0)
            {
                entry.LastResolvedHeight = h;
                entry.AverageResolvedHeight = entry.AverageResolvedHeight <= 0
                    ? h
                    : (0.7 * entry.AverageResolvedHeight) + (0.3 * h);
            }
        }
        EnforceCap();
        SaveAsync();
    }

    public void RecordFailure(string memKey, string strategyName) => RecordFailure(memKey, strategyName, StrategyFailureKind.Unknown);

    public void RecordFailure(string memKey, string strategyName, StrategyFailureKind kind)
    {
        if (string.IsNullOrEmpty(memKey) || string.IsNullOrEmpty(strategyName)) return;

        var now = DateTime.UtcNow;
        var list = _entries.GetOrAdd(memKey, _ => new List<StrategyMemoryEntry>());
        bool demoted = false;
        int threshold = DemoteThresholdFor(kind);
        lock (list)
        {
            var entry = list.FirstOrDefault(e => string.Equals(e.StrategyName, strategyName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new StrategyMemoryEntry { StrategyName = strategyName, FirstSeen = now, LastSuccess = DateTime.MinValue };
                list.Add(entry);
            }
            entry.FailureCount++;
            entry.ConsecutiveFailures++;
            entry.LastFailure = now;
            entry.LastFailureKind = kind;
            demoted = entry.ConsecutiveFailures == threshold;
        }
        if (demoted)
            _logger?.Info("[StrategyMemory] Strategy '" + strategyName + "' for " + memKey + " demoted after " + threshold + " consecutive " + kind + " failure(s) — next request will re-cascade.");
        EnforceCap();
        SaveAsync();
    }

    // Forget all memory for a host. UI can call this when the user manually clicks "forget".
    public void ForgetKey(string memKey)
    {
        _entries.TryRemove(memKey, out _);
        SaveAsync();
    }

    // Snapshot of the whole memory, keyed by host+streamType. Used by the UI's bypass-health view.
    public IReadOnlyDictionary<string, IReadOnlyList<StrategyMemoryEntry>> Snapshot()
    {
        var result = new Dictionary<string, IReadOnlyList<StrategyMemoryEntry>>();
        foreach (var kvp in _entries)
        {
            lock (kvp.Value) result[kvp.Key] = kvp.Value.ToList();
        }
        return result;
    }

    // Wrap memory in a versioned envelope so we can wipe the file on every app-version change.
    // Learned ranking is cheap to rebuild (~one cascade per host) but the risk of a stale bad
    // strategy surviving a behavior-changing update is expensive — e.g. a "success" recorded by
    // pre-playback-feedback code sticks around forever and keeps the fast-path locked on a broken
    // strategy. Wiping on every build is the simplest correctness guarantee: each new binary starts
    // with a clean slate and re-learns from real outcomes under the current logic.
    internal class MemoryEnvelope
    {
        public string? AppVersion { get; set; }
        public Dictionary<string, List<StrategyMemoryEntry>>? Entries { get; set; }
    }

    // Read the current app version from version.txt next to the exe. Returns null if the file is
    // missing or empty (dev builds from `dotnet run` without a version.txt → we won't wipe in that
    // case, so iterative debugging doesn't lose memory every compile).
    internal static string? ReadCurrentAppVersion(string basePath)
    {
        try
        {
            string versionPath = Path.Combine(basePath, "version.txt");
            if (!File.Exists(versionPath)) return null;
            string v = File.ReadAllText(versionPath).Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    public void Load()
    {
        string? currentVersion = ReadCurrentAppVersion(Path.GetDirectoryName(_path) ?? AppDomain.CurrentDomain.BaseDirectory);

        // Try primary → .bak fallback. If the primary was corrupted (partial write from a process
        // crash) the backup written before the last successful save is usually still intact.
        foreach (string candidate in new[] { _path, _path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                string json = File.ReadAllText(candidate);
                if (string.IsNullOrWhiteSpace(json)) continue;

                // Try the new envelope format first. Fall back to the legacy raw-dictionary format
                // (pre-version-gate) so users upgrading don't see a parse error — we'll just wipe
                // because the old format has no version tag.
                string? storedVersion = null;
                Dictionary<string, List<StrategyMemoryEntry>>? loaded = null;
                bool isLegacyFormat = false;
                try
                {
                    var envelope = JsonSerializer.Deserialize<MemoryEnvelope>(json);
                    if (envelope?.Entries != null)
                    {
                        storedVersion = envelope.AppVersion;
                        loaded = envelope.Entries;
                    }
                    else
                    {
                        // Envelope deserialized but had no entries — could be a legacy dict. Try raw.
                        loaded = JsonSerializer.Deserialize<Dictionary<string, List<StrategyMemoryEntry>>>(json);
                        isLegacyFormat = loaded != null;
                    }
                }
                catch
                {
                    loaded = JsonSerializer.Deserialize<Dictionary<string, List<StrategyMemoryEntry>>>(json);
                    isLegacyFormat = loaded != null;
                }

                if (loaded == null) continue;

                bool versionMismatch = currentVersion != null
                    && (isLegacyFormat || storedVersion == null || storedVersion != currentVersion);
                if (versionMismatch)
                {
                    _logger?.Info("[StrategyMemory] App version changed (" + (storedVersion ?? "<none>") + " → " + currentVersion + "). Wiping memory for a clean re-learn.");
                    _entries.Clear();
                    Save(); // persist the new envelope + empty entries under the current version
                    return;
                }

                foreach (var kvp in loaded)
                    _entries[kvp.Key] = kvp.Value ?? new List<StrategyMemoryEntry>();
                if (candidate.EndsWith(".bak"))
                    _logger?.Warning("[StrategyMemory] Primary file corrupt; recovered " + _entries.Count + " host entries from backup.");
                else
                    _logger?.Debug("[StrategyMemory] Loaded " + _entries.Count + " host entries (version " + (storedVersion ?? "unversioned") + ").");
                return;
            }
            catch (Exception ex)
            {
                _logger?.Warning("[StrategyMemory] Failed to parse " + Path.GetFileName(candidate) + ": " + ex.Message + " — trying next source.");
            }
        }
        // Neither primary nor backup worked — attempt migration from legacy tier_memory.json so
        // existing users don't lose their learned tier rankings on upgrade. Safe to call even if
        // the legacy file is absent.
        try { MigrateFromLegacyTierMemory(); }
        catch (Exception ex)
        {
            _logger?.Warning("[StrategyMemory] Starting fresh (no readable memory, migration failed): " + ex.Message);
        }
    }

    public void MigrateFromLegacyTierMemory()
    {
        string legacyPath = Path.Combine(Path.GetDirectoryName(_path) ?? "", "tier_memory.json");
        if (!File.Exists(legacyPath)) return;
        try
        {
            string json = File.ReadAllText(legacyPath);
            var legacy = JsonSerializer.Deserialize<Dictionary<string, LegacyTierMemoryEntry>>(json);
            if (legacy == null || legacy.Count == 0) return;

            foreach (var kvp in legacy)
            {
                string? strategy = TierGroupToCanonicalStrategy(kvp.Value.Tier);
                if (strategy == null) continue;
                var entry = new StrategyMemoryEntry
                {
                    StrategyName = strategy,
                    SuccessCount = kvp.Value.SuccessCount,
                    LastSuccess = kvp.Value.LastSuccess == default ? DateTime.UtcNow : kvp.Value.LastSuccess.ToUniversalTime(),
                    FirstSeen = kvp.Value.LastSuccess == default ? DateTime.UtcNow : kvp.Value.LastSuccess.ToUniversalTime(),
                };
                _entries[kvp.Key] = new List<StrategyMemoryEntry> { entry };
            }
            _logger?.Info("[StrategyMemory] Migrated " + legacy.Count + " entries from tier_memory.json.");
            Save();
        }
        catch (Exception ex)
        {
            _logger?.Warning("[StrategyMemory] Legacy tier_memory.json migration failed: " + ex.Message);
        }
    }

    private static string? TierGroupToCanonicalStrategy(string tier)
    {
        return tier switch
        {
            "tier0" or "tier0-streamlink" => "tier0:streamlink-native",
            "tier1" => "tier1:po+impersonate",
            "tier2" => "tier2:cloud-whyknot",
            "tier3" => "tier3:plain",
            _ => null
        };
    }

    public void Save()
    {
        try
        {
            lock (_saveLock)
            {
                var snapshot = _entries.ToDictionary(kvp => kvp.Key, kvp => {
                    lock (kvp.Value) return kvp.Value.ToList();
                });
                string? currentVersion = ReadCurrentAppVersion(Path.GetDirectoryName(_path) ?? AppDomain.CurrentDomain.BaseDirectory);
                var envelope = new MemoryEnvelope { AppVersion = currentVersion, Entries = snapshot };
                string json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });

                // Atomic-ish write: write to a sibling temp file, then move into place. Protects
                // against torn writes if the process dies mid-save. Before the move, promote the
                // current primary to .bak so Load() can recover if the move is itself interrupted.
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                try
                {
                    if (File.Exists(_path))
                    {
                        string bak = _path + ".bak";
                        try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                        try { File.Move(_path, bak); } catch { /* best-effort; still proceed */ }
                    }
                    File.Move(tmp, _path);
                }
                catch (Exception moveEx)
                {
                    // If the swap failed we at least still have the temp file on disk — try a
                    // direct overwrite as the last-ditch fallback.
                    try { File.Copy(tmp, _path, overwrite: true); File.Delete(tmp); }
                    catch { _logger?.Warning("[StrategyMemory] Atomic save failed: " + moveEx.Message); }
                }
            }
        }
        catch (Exception ex) { _logger?.Warning("[StrategyMemory] Save failed: " + ex.Message); }
    }

    private System.Threading.Tasks.Task? _pendingSave;
    private readonly object _pendingSaveLock = new();

    // Coalesce saves: multiple recordSuccess/recordFailure calls in quick succession produce one
    // disk write. Keeps per-request latency low without losing data.
    private void SaveAsync()
    {
        lock (_pendingSaveLock)
        {
            if (_pendingSave != null && !_pendingSave.IsCompleted) return;
            _pendingSave = System.Threading.Tasks.Task.Run(async () => {
                await System.Threading.Tasks.Task.Delay(250);
                Save();
            });
        }
    }

    // Synchronous flush for shutdown: wait for any pending coalesced save to finish, then force one
    // more synchronous save so the latest in-memory state is on disk. Process exits shouldn't lose
    // the last few recorded successes/failures.
    public void Flush()
    {
        System.Threading.Tasks.Task? pending;
        lock (_pendingSaveLock) { pending = _pendingSave; }
        try { pending?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore: we're about to save anyway */ }
        Save();
    }

    private void EnforceCap()
    {
        // Cap at 200 hosts (double the old tier-memory cap; we now hold lists). Evict the host whose
        // latest entry is oldest — that's the one we've touched least recently.
        if (_entries.Count <= 200) return;
        var oldestHost = _entries
            .Select(kvp => {
                DateTime latest;
                lock (kvp.Value) latest = kvp.Value.Count == 0 ? DateTime.MinValue : kvp.Value.Max(e => e.LastSuccess);
                return (kvp.Key, latest);
            })
            .OrderBy(t => t.latest)
            .FirstOrDefault();
        if (oldestHost.Key != null)
            _entries.TryRemove(oldestHost.Key, out _);
    }

    // Player is part of the key because AVPro and Unity have incompatible format requirements —
    // AVPro happily plays HLS/DASH, Unity needs progressive MP4. Without this split, a cloud-
    // based strategy that works great for AVPro on youtube.com:vod would poison Unity's
    // fast-path: memory says "cloud wins, use it first" but cloud's Unity output gets rejected
    // by the player. Keep the (url, isLive) overload for call sites that don't know the player
    // yet (migration path / legacy callers); it normalises to "unknown" which still segregates
    // from real entries.
    public static string KeyFor(string url, bool isLive, string? player)
    {
        try
        {
            string host = new Uri(url).Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            string streamType = isLive ? "live" : "vod";
            string playerSegment = string.IsNullOrWhiteSpace(player)
                ? "unknown"
                : player.ToLowerInvariant();
            return host + ":" + streamType + ":" + playerSegment;
        }
        catch { return ""; }
    }

    // Legacy 2-arg overload — preserved so tests and any external callers that don't thread the
    // player through still compile. Resolves to the "unknown" player bucket, which is fine
    // because those entries can't mismatch AVPro's or Unity's specific learning.
    public static string KeyFor(string url, bool isLive) => KeyFor(url, isLive, null);

    // Pre-resolution live-stream heuristic. The cascade previously set the memory key's
    // live-flag based solely on `streamlink --can-handle-url`, which says nothing about
    // *liveness* — only "streamlink has a plugin". When streamlink isn't installed (or
    // doesn't claim YouTube), every URL bucketed as `:vod:` and a YouTube /live/ URL
    // would inherit the VOD fast-path memory (e.g. tier2:cloud-whyknot) that hangs on
    // live formats. URL-pattern check catches the common live shapes before resolution.
    public static bool LooksLikeLive(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        try
        {
            var uri = new Uri(url);
            string path = uri.AbsolutePath.ToLowerInvariant();
            // youtube.com/live/<id>, /@handle/live, /channel/<id>/live, /c/<name>/live
            if (path.StartsWith("/live/")) return true;
            if (path.EndsWith("/live")) return true;
        }
        catch { }
        return false;
    }

    // Heuristic classifier. Maps raw error signals (exception + optional stderr/process exit
    // context) to a StrategyFailureKind. Keeps dispatcher call sites short and consistent.
    public static StrategyFailureKind ClassifyFailure(Exception? ex, string? stderr, bool timedOut = false)
    {
        if (timedOut) return StrategyFailureKind.Timeout;
        if (ex is OperationCanceledException) return StrategyFailureKind.Timeout;

        string blob = (stderr ?? "") + " " + (ex?.Message ?? "");
        if (string.IsNullOrWhiteSpace(blob)) return StrategyFailureKind.Unknown;
        string b = blob.ToLowerInvariant();

        // Block / ban signals
        if (b.Contains("http error 403") || b.Contains(" 403 ") || b.Contains("forbidden")) return StrategyFailureKind.Blocked403;
        if (b.Contains("http error 401") || b.Contains(" 401 ") || b.Contains("unauthorized")) return StrategyFailureKind.Blocked403;
        if (b.Contains("http error 429") || b.Contains(" 429 ") || b.Contains("too many requests")) return StrategyFailureKind.Blocked403;
        if (b.Contains("sign in to confirm") || b.Contains("confirm you're not a bot")) return StrategyFailureKind.JsChallenge;
        if (b.Contains("cloudflare") && (b.Contains("challenge") || b.Contains("just a moment"))) return StrategyFailureKind.JsChallenge;

        // Terminal signals
        if (b.Contains("http error 404") || b.Contains(" 404 ") || b.Contains("not found")) return StrategyFailureKind.NotFound404;
        if (b.Contains("http error 410") || b.Contains(" 410 ") || b.Contains("gone")) return StrategyFailureKind.NotFound404;
        if (b.Contains("http error 451") || b.Contains(" 451 ")) return StrategyFailureKind.NotFound404;
        if (b.Contains("video unavailable") || b.Contains("this video is not available")) return StrategyFailureKind.NotFound404;
        if (b.Contains("private video") || b.Contains("removed by the uploader")) return StrategyFailureKind.NotFound404;

        // Network signals
        if (b.Contains("timed out") || b.Contains("timeout")) return StrategyFailureKind.Timeout;
        if (b.Contains("name or service not known") || b.Contains("no address associated")) return StrategyFailureKind.NetworkError;
        if (b.Contains("connection refused") || b.Contains("connection reset")) return StrategyFailureKind.NetworkError;
        if (b.Contains("network is unreachable") || b.Contains("no route to host")) return StrategyFailureKind.NetworkError;
        if (b.Contains("ssl") && (b.Contains("handshake") || b.Contains("tlsv"))) return StrategyFailureKind.NetworkError;

        return StrategyFailureKind.Unknown;
    }

    private class LegacyTierMemoryEntry
    {
        public string Tier { get; set; } = "";
        public int SuccessCount { get; set; }
        public DateTime LastSuccess { get; set; }
    }
}
