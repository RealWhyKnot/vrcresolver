using System.Collections.Generic;
using System.Linq;

namespace WKVRCProxy.Core.Models;

// Default strategy priority list. The runtime default is `PriorityDefaults`; `PriorityDefaultsV1`
// and `PriorityDefaultsV2` are frozen historical snapshots used by SettingsManager to recognize
// "user is on an old default and hasn't customized" and infer not-overridden status when migrating
// pre-override-tracking configs (see SettingsManager.InferLegacyOverrides). They never grow.
//
// To change the default in the future: edit `PriorityDefaults`. Users whose config has
// `userOverriddenKeys` containing `"strategyPriority"` keep their list; everyone else picks up
// the new default on next load. No version bump, no new historical array.
public static class StrategyDefaults
{
    // V1: initial release — one per individual YouTube client, po-only separate. FROZEN.
    public static readonly string[] PriorityDefaultsV1 = new[]
    {
        "tier1:yt-combo",
        "tier2:cloud-whyknot",
        "tier1:po-only",
        "tier1:web-safari",
        "tier1:ios-music",
        "tier1:mweb",
        "tier1:tv-embedded",
        "tier1:android-vr",
        "tier1:default",
        "tier1:vrchat-ua",
        "tier1:impersonate-only",
        "tier1:plain",
        "tier1:browser-extract",
        "tier3:plain",
    };

    // V2: yt-combo covers every player_client internally; per-client strategies removed; added
    // tier1:ipv6. FROZEN — used by legacy-config inference.
    public static readonly string[] PriorityDefaultsV2 = new[]
    {
        "tier1:yt-combo",
        "tier2:cloud-whyknot",
        "tier1:ipv6",
        "tier1:default",
        "tier1:vrchat-ua",
        "tier1:impersonate-only",
        "tier1:plain",
        "tier1:browser-extract",
        "tier3:plain",
    };

    // Current default. Edit this to change what fresh configs (and configs without
    // userOverriddenKeys["strategyPriority"]) get on next load. The warp+ variants slot in just
    // before tier3:plain to match their built-in priority numbers (80/90/95/tier3).
    public static readonly string[] PriorityDefaults = new[]
    {
        "tier1:yt-combo",        // one process, 11 clients tried internally
        "tier2:cloud-whyknot",   // cross-IP fallback
        "tier1:ipv6",            // route around v4 rate limits
        "tier1:default",         // non-YouTube hosts (auto PO + impersonate)
        "tier1:vrchat-ua",       // movie-world allowlisted clients
        "tier1:impersonate-only",// TLS-fingerprint-sensitive origins
        "tier1:plain",           // bare yt-dlp last-resort
        "tier1:browser-extract", // JS-gated sites
        "tier1:warp+default",    // direct, but routed through Cloudflare WARP egress
        "tier1:warp+vrchat-ua",  // VRChat UA + WARP egress
        "tier3:plain",           // original VRChat yt-dlp-og
    };

    // Best-known YouTube player_client ordering for the yt-combo's internal retry list. Matches
    // community sentiment as of early 2026 — TV-family first (most bot-resistant), then the
    // original server default web clients, then mobile clients, then Android. yt-dlp stops at the
    // first client that returns a usable format, so the head of this list matters most.
    public static readonly string[] YouTubeComboClientOrderDefault = new[]
    {
        "tv_simply",
        "tv_embedded",
        "tv",
        "web_safari",
        "web",
        "mweb",
        "ios",
        "ios_music",
        "android",
        "android_vr",
        "android_music",
    };

    // Returns true iff the saved list exactly equals one of the frozen historical defaults
    // (V1 or V2). Used by SettingsManager.InferLegacyOverrides to decide whether a pre-override-
    // tracking config was at the default for its day (→ not overridden) or was hand-edited
    // (→ overridden, preserve verbatim).
    public static bool MatchesAnyHistoricalPriorityDefault(List<string>? saved)
    {
        if (saved == null) return false;
        if (saved.SequenceEqual(PriorityDefaultsV1)) return true;
        if (saved.SequenceEqual(PriorityDefaultsV2)) return true;
        if (saved.SequenceEqual(PriorityDefaults)) return true;
        return false;
    }

    public static bool MatchesYouTubeComboDefault(List<string>? saved)
    {
        if (saved == null) return false;
        return saved.SequenceEqual(YouTubeComboClientOrderDefault);
    }
}
