using System.Collections.Generic;
using System.Linq;

namespace WKVRCProxy.Core.Models;

// Central registry of "what the default strategy priority list looks like" across versions.
// When we learn a better ordering or add/remove strategies, we bump CurrentVersion and append a
// new default list. Users whose saved StrategyPriority exactly equals any PREVIOUS version's
// default auto-migrate to the current default on load. Lists that don't match any known default
// are treated as "user-customized" and preserved verbatim.
//
// Never edit a historical entry — it's load-bearing for migration comparison. Only append new
// ones and bump CurrentVersion.
public static class StrategyDefaults
{
    // Bump whenever the default ordering meaningfully changes.
    public const int CurrentVersion = 2;

    // V1: initial release — one per individual YouTube client, po-only separate.
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

    // V2: expanded tier1:yt-combo now covers every player_client internally (one subprocess tries
    // them all in order, stopping at first success). The per-client strategies are removed — they
    // were redundant and inflated burst count. Added tier1:ipv6 for network-layer routing around
    // v4-only IP flags.
    public static readonly string[] PriorityDefaultsV2 = new[]
    {
        "tier1:yt-combo",        // one process, 11 clients tried internally
        "tier2:cloud-whyknot",   // cross-IP fallback
        "tier1:ipv6",            // route around v4 rate limits
        "tier1:default",         // non-YouTube hosts (auto PO + impersonate)
        "tier1:vrchat-ua",       // movie-world allowlisted clients
        "tier1:impersonate-only",// TLS-fingerprint-sensitive origins
        "tier1:plain",           // bare yt-dlp last-resort
        "tier1:browser-extract", // JS-gated sites
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

    // Migration: if `saved` exactly matches any known OLD default, return the current default.
    // If `saved` matches the current default, return it unchanged. If it doesn't match any known
    // default (user has customized), return `saved` unchanged. Return value indicates whether a
    // migration occurred so the caller can log + persist.
    public static bool TryMigratePriorityList(List<string>? saved, int savedVersion, out List<string> migrated)
    {
        if (savedVersion >= CurrentVersion || saved == null)
        {
            migrated = saved ?? new List<string>(PriorityDefaultsV2);
            return false;
        }

        // Build a lookup of historical defaults by version. Extend this as new versions ship.
        var history = new Dictionary<int, string[]>
        {
            { 1, PriorityDefaultsV1 },
            { 2, PriorityDefaultsV2 },
        };

        if (history.TryGetValue(savedVersion, out var oldDefault)
            && saved.SequenceEqual(oldDefault))
        {
            migrated = new List<string>(PriorityDefaultsV2);
            return true;
        }

        // Saved list doesn't match the historical default for its declared version — user has
        // customized. Don't overwrite their choices; just stamp the version forward so we don't
        // re-check on every load.
        migrated = saved;
        return false;
    }
}
