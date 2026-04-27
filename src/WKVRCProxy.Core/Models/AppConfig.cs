using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WKVRCProxy.Core.Models;

public class HistoryEntry
{
    public DateTime Timestamp { get; set; }
    public string OriginalUrl { get; set; } = string.Empty;
    public string ResolvedUrl { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public string Player { get; set; } = string.Empty;
    public bool Success { get; set; }

    // No [JsonPropertyName] — C# default serialization outputs PascalCase matching the TypeScript HistoryEntry interface.
    public bool IsLive { get; set; }

    public string StreamType { get; set; } = "unknown";

    public int? ResolutionHeight { get; set; }
    public int? ResolutionWidth { get; set; }
    public string? Vcodec { get; set; }

    // Post-hoc playback verification. `Success` means "resolution returned a URL" — which the old
    // tier2:cloud-whyknot bug showed isn't the same as "AVPro played it". `PlaybackVerified` is
    // set by the feedback loop:
    //   true  — N seconds elapsed after resolution with no AVPro "Loading failed" observed
    //   false — AVPro emitted "Loading failed" for this entry's URL, OR pre-flight probe rejected
    //   null  — still pending verification (fresh entry) or we have no way to verify (tier4 passthrough)
    public bool? PlaybackVerified { get; set; }
}

public class AppConfig
{
    [JsonPropertyName("debugMode")]
    public bool DebugMode { get; set; } = true;

    [JsonPropertyName("preferredResolution")]
    public string PreferredResolution { get; set; } = "1080p";

    [JsonPropertyName("forceIPv4")]
    public bool ForceIPv4 { get; set; } = false;

    [JsonPropertyName("autoPatchOnStart")]
    public bool AutoPatchOnStart { get; set; } = true;

    [JsonPropertyName("userAgent")]
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    [JsonPropertyName("history")]
    public List<HistoryEntry> History { get; set; } = new();

    [JsonPropertyName("preferredTier")]
    public string PreferredTier { get; set; } = "tier1";

    [JsonPropertyName("customVrcPath")]
    public string? CustomVrcPath { get; set; }

    [JsonPropertyName("bypassHostsSetupDeclined")]
    public bool BypassHostsSetupDeclined { get; set; } = false;

    [JsonPropertyName("enableRelayBypass")]
    public bool EnableRelayBypass { get; set; } = true;

    [JsonPropertyName("disabledTiers")]
    public List<string> DisabledTiers { get; set; } = new();

    [JsonPropertyName("enableTierMemory")]
    public bool EnableTierMemory { get; set; } = true;

    // Tier 2 (whyknot.dev cloud resolver) does PO-token / extractor orchestration and can
    // legitimately take 30-60s on cold cache. The old 10s default caused false timeouts.
    [JsonPropertyName("tier2TimeoutSeconds")]
    public int Tier2TimeoutSeconds { get; set; } = 60;

    // Check GitHub for a newer yt-dlp on every launch and swap the binary if one is available.
    // Never touches yt-dlp-og.exe (Tier 3 needs VRChat's pinned copy).
    [JsonPropertyName("autoUpdateYtDlp")]
    public bool AutoUpdateYtDlp { get; set; } = true;

    // Enable the browser-extract bypass strategy (headless Chromium/Edge/Chrome). Fires when
    // yt-dlp strategies can't crack a JS-gated site. Default on — system browsers are detected
    // first, no download unless DownloadBundledChromium is also true.
    [JsonPropertyName("enableBrowserExtract")]
    public bool EnableBrowserExtract { get; set; } = true;

    // When no Edge/Chrome/Brave is found on the system, opt in to downloading a ~180 MB bundled
    // Chromium via PuppeteerSharp's BrowserFetcher. Off by default to avoid surprise downloads;
    // users who lack a system browser and want browser-extract enable this once.
    [JsonPropertyName("downloadBundledChromium")]
    public bool DownloadBundledChromium { get; set; } = false;

    // Hosts that must NOT be relay-wrapped because they only accept AVPro's native UnityPlayer UA
    // (VRChat "movie worlds" like vr-m.net). Everything else is wrapped by default — the relay wrap
    // is what bypasses VRChat's trusted-URL allowlist. See feedback_relay_purpose memory.
    [JsonPropertyName("nativeAvProUaHosts")]
    public List<string> NativeAvProUaHosts { get; set; } = new() { "vr-m.net" };

    // Before handing a resolved URL to AVPro, HEAD/GET-probe it to confirm it returns 2xx and has
    // real content. An unhealthy probe demotes the winning strategy and re-cascades. Catches cases
    // where a tier returns a URL the upstream rejects (403/404) or is unreachable.
    [JsonPropertyName("enablePreflightProbe")]
    public bool EnablePreflightProbe { get; set; } = true;

    // Ordered list of strategy names the cold-race consults before falling back to memory ranking
    // or the built-in priority numbers. Users can reorder this in Settings to make a specific
    // strategy wave-1 primary. Strategies missing from this list still run (at the tail, ordered
    // by their built-in priority).
    //
    // Default-syncing: on load, if "strategyPriority" is NOT in UserOverriddenKeys, this field
    // gets reset to StrategyDefaults.PriorityDefaults. Once the user touches the list in the UI,
    // the UI adds "strategyPriority" to UserOverriddenKeys and the user's choices are preserved
    // verbatim across launches and default updates. See SettingsManager.SyncDefaultsForNonOverriddenFields.
    [JsonPropertyName("strategyPriority")]
    public List<string> StrategyPriority { get; set; } = new(StrategyDefaults.PriorityDefaults);

    // Ordered YouTube player_client list the `tier1:yt-combo` strategy passes to yt-dlp. yt-dlp
    // tries these in order and stops at the first client that returns a usable format, so we get
    // exactly ONE subprocess per YouTube play in the common case. Power users can reorder to put
    // a known-working client first for their network (e.g. if tv_simply always wins on YouTube
    // for them, moving it to position 0 saves ~50ms of internal retries).
    [JsonPropertyName("youtubeComboClientOrder")]
    public List<string> YouTubeComboClientOrder { get; set; } = new(StrategyDefaults.YouTubeComboClientOrderDefault);

    // Wave-based dispatch parameters for the cold race. Wave N fires WaveSize strategies in
    // parallel; if none win within WaveStageDeadlineSeconds, the next wave kicks off (carrying
    // still-pending strategies along). Lower burst factor against rate-limited APIs like YouTube.
    // Disabling reverts to the legacy "fire everything at once" race.
    [JsonPropertyName("enableWaveRace")]
    public bool EnableWaveRace { get; set; } = true;

    [JsonPropertyName("waveSize")]
    public int WaveSize { get; set; } = 2;

    [JsonPropertyName("waveStageDeadlineSeconds")]
    public int WaveStageDeadlineSeconds { get; set; } = 3;

    // Per-host rate limit: max yt-dlp (tier-1) spawns per this window before cold-race skips
    // tier 1 entirely and goes straight to cloud. Cloud + already-in-flight requests don't count.
    // Matches yt-dlp maintainer guidance of 2–3 concurrent max.
    [JsonPropertyName("perHostRequestBudget")]
    public int PerHostRequestBudget { get; set; } = 3;

    [JsonPropertyName("perHostRequestWindowSeconds")]
    public int PerHostRequestWindowSeconds { get; set; } = 10;

    // Twitch ad-segment handling for the Tier 0 (Streamlink) path. Streamlink can either:
    //   false (default) — pass the ad segments through. The viewer sees Twitch ads play, then the
    //                     real stream resumes seamlessly with no pause.
    //   true            — filter the ad segments out and emit an HLS discontinuity in their place.
    //                     AVPro stalls on the last good frame for the duration of the ad break,
    //                     then resumes with the real stream. Note: this does NOT skip ahead in
    //                     time; the player simply freezes the picture until ads are over.
    [JsonPropertyName("streamlinkDisableTwitchAds")]
    public bool StreamlinkDisableTwitchAds { get; set; } = false;

    // Log per-segment relay timing (TTFB + throughput) at debug level, and surface stalls/slow
    // segments at warning level. Helps diagnose stutter that isn't a hard playback failure —
    // long upstream TTFB or low throughput on a CDN segment will show up here even when the
    // stream eventually resumes.
    [JsonPropertyName("enableRelaySmoothnessDebug")]
    public bool EnableRelaySmoothnessDebug { get; set; } = true;

    // When true, every origin-facing resolution strategy (yt-dlp tier 1, browser-extract,
    // yt-dlp-og tier 3) egresses through the local Cloudflare WARP SOCKS5 proxy, masking the
    // user's real IP from origin servers. Tier 2 (whyknot.dev) intentionally stays direct — it's
    // a trusted endpoint and adding WARP just for the call home would tax the cloud resolver and
    // add latency for no privacy gain.
    //
    // Requires the bundled tools/warp/wgcf.exe + wireproxy.exe. When WARP can't start, strategies
    // abort with a "WARP unavailable" warning rather than silently leaking the real IP.
    //
    // Note: switching from off→on while a session is active doesn't retroactively re-route
    // already-in-flight requests, and origin-side IP-binding (notably YouTube) may briefly
    // require a fresh resolution pass after the toggle.
    [JsonPropertyName("maskIp")]
    public bool MaskIp { get; set; } = false;

    // Opt-in: when the cascade fully fails (every strategy unable to resolve), POST a sanitized
    // failure summary to whyknot.dev/api/report so the Discord channel sees the pattern. Default
    // off; the first time the cascade fails after install, a modal asks the user to opt in. The
    // sanitizer strips usernames, paths, and IP addresses; only the URL's domain (not path/query)
    // is sent, plus a SHA256-prefix hash of the path so duplicates can be correlated without
    // leaking which video was being played.
    [JsonPropertyName("enableAnonymousReporting")]
    public bool EnableAnonymousReporting { get; set; } = false;

    // Set to true once the user has answered the opt-in prompt (yes or no). Suppresses the modal
    // on subsequent cascade failures. The user can still toggle EnableAnonymousReporting in
    // Settings after answering.
    [JsonPropertyName("anonymousReportingPromptAnswered")]
    public bool AnonymousReportingPromptAnswered { get; set; } = false;

    // Surfaces a "Website" tab that frames https://whyknot.dev inside the program. PoC stage —
    // dark by default; flip to true in app_config.json to test. Design + migration plan live
    // in docs/embed-website/. The tab does not yet expose a native bridge to the embedded page
    // (Phase 2). Off in default builds; not surfaced in Settings on purpose.
    [JsonPropertyName("enableWebsiteTab")]
    public bool EnableWebsiteTab { get; set; } = false;

    // Names (in JSON-key form) of fields the user has explicitly customized via the UI. On load,
    // SettingsManager re-pulls the current code default for any default-tracked field NOT in this
    // set, so editing a default constant in source automatically flows out to all users who
    // haven't customized that field. See Models.DefaultTrackedFields for the registry of
    // affected fields.
    //
    // The UI is responsible for adding/removing entries (markOverridden/clearOverridden in
    // appStore.ts). Legacy configs without this field get their override state inferred on first
    // load by comparing values to historical defaults (see SettingsManager.InferLegacyOverrides).
    [JsonPropertyName("userOverriddenKeys")]
    public HashSet<string> UserOverriddenKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// Registry of AppConfig fields whose default values should track the embedded-source defaults
// when the user hasn't explicitly customized them. SettingsManager iterates this on load: for
// each entry whose JSON key is absent from AppConfig.UserOverriddenKeys, the resetter runs.
//
// To add a new default-tracked field: add a `Resetters` entry mapping its JSON key (camelCase,
// matches [JsonPropertyName]) to an Action that copies the current code default into the
// AppConfig instance. Optionally add a `LegacyMatchers` entry — a predicate that returns true
// iff the saved value matches a known historical default — so pre-override-tracking configs
// can be auto-classified as not-overridden on first load.
public static class DefaultTrackedFields
{
    public static readonly Dictionary<string, Action<AppConfig>> Resetters =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "strategyPriority",        c => c.StrategyPriority = new List<string>(StrategyDefaults.PriorityDefaults) },
        { "youtubeComboClientOrder", c => c.YouTubeComboClientOrder = new List<string>(StrategyDefaults.YouTubeComboClientOrderDefault) },
        { "nativeAvProUaHosts",      c => c.NativeAvProUaHosts = new List<string> { "vr-m.net" } },
    };

    // Predicates used by SettingsManager.InferLegacyOverrides for configs missing the
    // userOverriddenKeys field entirely. Returns true if the field's loaded value matches any
    // shipped historical default (so the user is "still on a default") — the field is then NOT
    // marked overridden. A mismatch means the user customized at some point — the field IS
    // marked overridden and its value is preserved.
    public static readonly Dictionary<string, Func<AppConfig, bool>> LegacyMatchers =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "strategyPriority",        c => StrategyDefaults.MatchesAnyHistoricalPriorityDefault(c.StrategyPriority) },
        { "youtubeComboClientOrder", c => c.YouTubeComboClientOrder == null
                                           || c.YouTubeComboClientOrder.Count == 0
                                           || StrategyDefaults.MatchesYouTubeComboDefault(c.YouTubeComboClientOrder) },
        // nativeAvProUaHosts has only ever shipped one default; an empty list also reads as default.
        { "nativeAvProUaHosts",      c => c.NativeAvProUaHosts == null
                                           || (c.NativeAvProUaHosts.Count == 1 && c.NativeAvProUaHosts[0] == "vr-m.net") },
    };
}
