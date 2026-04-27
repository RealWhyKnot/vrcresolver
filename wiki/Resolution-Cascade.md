# Resolution Cascade

The cascade is the heart of WKVRCProxy. Source: [`src/WKVRCProxy.Core/Services/ResolutionEngine.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/ResolutionEngine.cs).

## Tiers and strategies

| Tier | Strategy | What it does | When |
|---|---|---|---|
| `tier0` | `tier0:streamlink` | Twitch / live HLS resolver via vendored `streamlink.exe`. Optionally filters Twitch ad segments (stalls picture, doesn't skip time). | Twitch + similar live hosts |
| `tier1` | `tier1:plain` | Vanilla yt-dlp, system UA. The fast path. | Always in race |
| `tier1` | `tier1:pot` | yt-dlp + PO token via the bgutil sidecar. | YouTube, when DomainRequiresPot is set |
| `tier1` | `tier1:impersonate` | yt-dlp wrapped behind `curl-impersonate-win.exe` (Chrome 116 TLS fingerprint). | TLS-fingerprint-gated hosts |
| `tier1` | `tier1:vrchat-ua` | yt-dlp with VRChat's UnityPlayer UA. | Hosts that gate on UA |
| `tier1` | `tier1:mweb` | yt-dlp with `--player-client mweb`. | YouTube mobile-only paths |
| `tier1` | `tier1:yt-combo` | yt-dlp with `--player-client web_safari,web,mweb`. | Default wave-1 primary for YouTube |
| `tier1` | `tier1:browser-extract` | Headless Chromium/Edge/Chrome via PuppeteerSharp; captures media URL + cookies + headers. | JS-challenge bypass |
| `tier1` | `tier1:warp+default` | yt-dlp routed through Cloudflare WARP SOCKS5. | When egress IP is blocked |
| `tier1` | `tier1:warp+vrchat-ua` | WARP + UnityPlayer UA. | Egress + UA gating combined |
| `tier2` | `tier2:cloud-whyknot` | Async WebSocket call to `whyknot.dev` cloud resolver. 60 s timeout (was 10 s — caused false timeouts). | Always in race in parallel with tier1 |
| `tier3` | `tier3:yt-dlp-og` | VRChat's *original* `yt-dlp.exe` (backed up at first patch as `yt-dlp-og.exe`). | After tier1 + tier2 both fail |
| `tier4` | `tier4:passthrough` | Returns the original URL pristine, no wrapping, no verification. | Backstop. Cannot be disabled. |

Strategies are toggled in **Settings → Advanced → Individual Strategies**. The order of cold-race waves comes from `AppConfig.strategyPriority`, with sensible defaults in `StrategyDefaults.PriorityDefaults`. Editing the constant at the source flows out to all users who haven't customized the list (see Settings Reference → `userOverriddenKeys`).

## Mask IP (always-WARP egress)

`Settings → Network → Mask IP` is a single toggle that forces every origin-facing strategy through Cloudflare WARP — tier 1 yt-dlp, tier 3 yt-dlp-og, and the headless browser-extract. Tier 2 (`whyknot.dev`) intentionally stays direct: it's a trusted endpoint, and routing the cloud call through WARP would just add latency for no privacy gain.

When `MaskIp = true` and WARP can't start (binaries missing, port collision, wgcf register failure), strategies abort with a "Mask IP is on but WARP is unavailable" warning rather than silently leaking the real IP. WARP eager-starts on launch and on toggle off→on so the first request after enabling doesn't pay wireproxy's cold-start latency.

The standalone `tier1:warp+default` and `tier1:warp+vrchat-ua` variants are suppressed from the cold-race catalog while Mask IP is on — they'd be byte-identical duplicates of `tier1:default` and `tier1:vrchat-ua` (which now both route through WARP).

## Fast path: `StrategyMemory`

Source: [`StrategyMemory.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/StrategyMemory.cs).

Per-host learning, keyed `host:streamType` (e.g., `youtube.com:vod`, `twitch.tv:live`). Each entry tracks:

- `SuccessCount`, `FailureCount`, `ConsecutiveFailures`
- `LastFailureKind` (enum: `NetworkError`, `Timeout`, `Blocked403`, `NotFound404`, `JsChallenge`, `LowQuality`, `PlaybackFailed`)
- `LastResolvedHeight`, `AverageResolvedHeight` (EMA, `0.7 × old + 0.3 × new`)
- Timestamps of last success / failure

**Ranking** (`GetPreferred`):
1. `NetScore = SuccessCount - FailureCount > 0`
2. Then by `AverageResolvedHeight` (we prefer 1080p over 360p)
3. Then by recency of last success
4. Excluded if past kind-specific demote threshold:

| Failure kind | Threshold |
|---|---|
| `PlaybackFailed`, `NotFound404` | 1 |
| `Blocked403`, `JsChallenge` | 2 |
| `LowQuality` | 3 |
| `Timeout`, `NetworkError` | 5 |

Entries older than 30 days are also excluded. Persisted to `strategy_memory.json` next to the exe.

> **The memory is wiped on version change.** `build.ps1` writes `dist/version.txt`; on startup the engine compares it to the version stamped into the existing memory file and wipes if different. Dev runs without `version.txt` are exempt — iteration shouldn't be lossy.

## Cold race: wave dispatch

When there's no fast-path winner, the engine kicks the cold race. Wave-race (default) fires `WaveSize` strategies (default 2) in parallel, waits `WaveStageDeadlineSeconds` (default 3 s), and if no winner, fires the next wave with the remaining strategies. Tier2 always runs in parallel from the start (its own 60 s deadline).

Settings: `EnableWaveRace`, `WaveSize`, `WaveStageDeadlineSeconds`.

The race completes when **any** strategy returns a URL — not necessarily a *good* URL. That's what the pre-flight probe and the playback-feedback loop are for.

## Pre-flight probe

`CheckUrlReachable()` sends:
- `HEAD` or `GET Range: bytes=0-4095` for binary media
- `GET` with HLS-shaped `Accept` headers for `.m3u8`

Uses `curl-impersonate-win.exe` if available (Chrome TLS fingerprint), falls back to plain `HttpClient` with matching headers. Accepts `2xx`, `3xx`, `416 Range Not Satisfiable`, or timeout (benefit of doubt). Rejects `4xx` / `5xx`.

Toggle via `EnablePreflightProbe`. Adds up to 5 s on cold resolves.

## Playback-feedback demote loop

Source: [`VrcLogMonitor.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/VrcLogMonitor.cs) + `ResolutionEngine.HandleAvProLoadFailure`.

1. `VrcLogMonitor` tails VRChat's output log.
2. On `[AVProVideo] Error: Loading failed`, looks up the failed URL in `_recentByUrl` (a 60 s ring keyed on original / resolved / upstream URLs).
3. Calls `RecordStrategyFailure(memKey, strategyName, StrategyFailureKind.PlaybackFailed)`.
4. `StrategyMemory.RecordFailure()` sets `ConsecutiveFailures = 1`, `LastFailureKind = PlaybackFailed`.
5. PlaybackFailed has demote threshold 1 → next request for that host re-cascades, skipping the fast-path.
6. Resolve-cache entries that would replay the dead URL are evicted.
7. `SystemEventBus.PublishStrategyDemoted` fires; the UI shows a red `Demoted` chip in the Logs view.

The relay also signals demotion: if AVPro aborts the relay connection with <256 KB received and that happens 3+ times in 30 s, the relay treats it as a Unity-format-rejection and triggers the same `PlaybackFailed` path.

## `PlaybackVerifyDelay`

Defaults to 8 s. If no `Loading failed` arrives within 8 s of returning the URL, the history entry is auto-promoted to `PlaybackVerified = true`. No news is good news. The successful strategy gets a success record in memory.

## "Why we wrap" — the relay

The pristine URL almost never plays directly. AVPro silently rejects anything off VRChat's narrow trusted-host list. So the engine's last step before returning to VRChat is `ApplyRelayWrap()` — see [[Relay Server]] for details.

The only times the wrap is skipped:
- The host already matches VRChat's trusted-host list (the URL plays pristine).
- The host is in `NativeAvProUaHosts` (default `["vr-m.net"]`) — these need to see traffic from AVPro's UnityPlayer UA, and the relay would corrupt that.
- `EnableRelayBypass` is `false` (manual override).
- The hosts-file mapping `127.0.0.1 localhost.youtube.com` isn't active (UAC declined).

## Per-host rate limiting

`PerHostRequestBudget` (default 3) and `PerHostRequestWindowSeconds` (default 10) prevent a single misbehaving world from spawning a swarm of yt-dlp processes for the same host. Beyond the budget, the engine returns the most recent cached result rather than spawning more strategies.

## Resolve cache

Keyed on `(player, normalized URL)`. Hit returns the cached resolution immediately (no probe, no race). Eviction:
- `PlaybackFailed` demotion of the strategy that produced the entry
- Manual cache clear in Settings
- Cache TTL (per-entry, varies by tier; cloud entries shorter than local)

## Streamlink negative-cache

`StreamlinkClient` keeps a per-host capability cache. Negative answers (Streamlink can't handle this host) expire after 24 h; positive answers after 7 d. So a host that Streamlink failed on gets retried daily, and a known-good host is cached longer.
