# Settings Reference

Source: [`src/WKVRCProxy.Core/Models/AppConfig.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Models/AppConfig.cs).

Settings live in `app_config.json` next to the exe. Most are exposed in **Settings** in the UI; some are advanced and edit-only.

## Core toggles

| Field | Type | Default | What it does |
|---|---|---|---|
| `debugMode` | bool | `true` | Verbose logging in `wkvrcproxy_*.log` |
| `autoPatchOnStart` | bool | `true` | Auto-swap VRChat's `yt-dlp.exe` on launch (the patcher loop) |
| `enableRelayBypass` | bool | `true` | Allow relay wrapping. **Setting this false will break most playback.** |
| `enableTierMemory` | bool | `true` | Per-host strategy learning |
| `enablePreflightProbe` | bool | `true` | Probe resolved URLs before handoff (catches dead cloud URLs; adds up to 5 s on cold) |
| `enableBrowserExtract` | bool | `true` | Headless-browser JS-challenge bypass |
| `downloadBundledChromium` | bool | `false` | Fetch ~180 MB Puppeteer Chromium if no system browser is found |
| `autoUpdateYtDlp` | bool | `true` | Check GitHub for newer yt-dlp + bgutil plugin on launch |

## Strategy and race tuning

| Field | Type | Default | What it does |
|---|---|---|---|
| `disabledTiers` | `List<string>` | `[]` | Tiers to skip entirely (e.g., `["tier0"]`) |
| `strategyPriority` | `List<string>` | `StrategyDefaults.PriorityDefaultsV2` | Cold-race wave-1 primaries, in order |
| `strategyPriorityDefaultsVersion` | int | `StrategyDefaults.CurrentVersion` | Migration version; default list auto-upgrades when the in-code constant bumps |
| `youtubeComboClientOrder` | `List<string>` | `StrategyDefaults.YouTubeComboClientOrderDefault` | `--player-client` order for `tier1:yt-combo` |
| `enableWaveRace` | bool | `true` | Staggered (wave) cold-race vs. all-at-once |
| `waveSize` | int | `2` | Strategies per wave |
| `waveStageDeadlineSeconds` | int | `3` | Seconds per wave before kicking the next |
| `tier2TimeoutSeconds` | int | `60` | Tier2 (cloud) deadline; was 10 s, caused false timeouts |

## Per-host rate limit

| Field | Type | Default | What it does |
|---|---|---|---|
| `perHostRequestBudget` | int | `3` | Max tier1 spawns per host inside the rolling window |
| `perHostRequestWindowSeconds` | int | `10` | Window length for the budget |

Beyond budget, the engine returns the most recent cached result rather than spawning more strategies. Protects against worlds that retry-loop a failing URL.

## Network / paths

| Field | Type | Default | What it does |
|---|---|---|---|
| `customVrcPath` | `string?` | `null` | Override VRChat Tools dir |
| `forceIPv4` | bool | `false` | Restrict DNS resolution to IPv4 |
| `userAgent` | string | Chrome 120 UA | Custom UA for pre-flight probes |
| `bypassHostsSetupDeclined` | bool | `false` | Set to true after a UAC decline so we don't keep prompting |
| `nativeAvProUaHosts` | `List<string>` | `["vr-m.net"]` | Hosts that need to see AVPro's UA directly; relay wrap is skipped for these |

## Streamlink

| Field | Type | Default | What it does |
|---|---|---|---|
| `streamlinkDisableTwitchAds` | bool | `false` | Filter Twitch ad segments. Stalls the picture, doesn't skip time. |

## Diagnostics

| Field | Type | Default | What it does |
|---|---|---|---|
| `enableRelaySmoothnessDebug` | bool | `true` | Log relay segment TTFB / throughput at debug-warn level |

## Other

| Field | Type | Default | What it does |
|---|---|---|---|
| `preferredResolution` | string | `"1080p"` | Informational; not enforced |
| `preferredTier` | string | `"tier1"` | Informational |
| `history` | `List<HistoryEntry>` | `[]` | Resolution history (UI history tab) |

## Editing tips

- `app_config.json` is read at startup and written on most settings changes. Stop the app before editing by hand.
- `nativeAvProUaHosts` accepts bare hostnames (e.g., `vr-m.net`); no protocol or path. Don't add hosts speculatively — see [[Relay Server]].
- `strategyPriority` names must match the strategy IDs in `StrategyDefaults.cs`. Misspellings are silently ignored.
- After a major version bump, `strategy_memory.json` is wiped; existing `app_config.json` is **not**.
