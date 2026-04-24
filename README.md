# WKVRCProxy

A Windows desktop app that makes VRChat's in-world video players resolve and play URLs reliably — including sources VRChat's built-in resolver can't handle, URLs blocked by bot detection, and hosts that aren't on VRChat's trusted allowlist.

> **Status:** alpha. It works, but expect rough edges. Run it alongside VRChat; shut it down if something misbehaves.

## What it does

VRChat's in-game video players resolve URLs with a bundled `yt-dlp.exe` and play them through AVPro. Two things break that pipeline in practice:

1. **AVPro's trusted-URL list.** Only a narrow set of hosts (`*.youtube.com`, `*.vimeo.com`, `*.vrcdn.live`, etc.) are allowed. Anything else is silently rejected with `[AVProVideo] Error: Loading failed.`
2. **Anti-bot detection.** YouTube increasingly requires PO tokens, valid visitor_data binding, and a real browser TLS fingerprint. The vanilla yt-dlp that VRChat ships can't provide any of that.

WKVRCProxy sits in front of VRChat's resolver pipeline and fixes both problems:

- A **hosts-file mapping** `127.0.0.1 localhost.youtube.com` routes any URL we "wrap" through a **local relay**. AVPro sees the trusted `*.youtube.com` host and plays it; our relay proxies the real target. Hosts already on VRChat's allowlist pass through pristine. A narrow deny-list handles hosts (like `vr-m.net`) that need to see traffic coming directly from the in-game player.
- A **tiered resolution cascade** (local yt-dlp → cloud resolver → VRChat's pinned yt-dlp → passthrough) tries multiple strategies in parallel for each URL, with per-host learning that remembers which strategy worked for which host. PO-token minting (via the `bgutil-ytdlp-pot-provider` sidecar + its yt-dlp plugin), `curl-impersonate` TLS fingerprinting, and an optional headless browser strategy cover the bot-detection cases.
- A **playback-feedback loop** watches VRChat's output log. When AVPro reports `Loading failed` on a URL we resolved, the responsible strategy is demoted immediately, the cache entry is evicted, and the next request re-cascades. This prevents the engine from locking onto a strategy that "returned a URL" but whose URLs don't actually play.
- A **pre-flight probe** verifies each resolved URL is reachable (using the same UA/headers AVPro would send) before we hand it back to VRChat.

## Architecture

Three .NET projects plus a Vue frontend:

```
WKVRCProxy.UI/          Photino desktop shell (WebView2)
 ├── Program.cs         IPC bridge: webmessages ↔ SystemEventBus
 └── ui/                Vue 3 + TypeScript + Tailwind + Pinia
     └── src/
         ├── views/     DashboardView, LogsView, HistoryView, SettingsView, …
         ├── stores/    appStore.ts (Pinia)
         └── components/

WKVRCProxy.Core/        The engine. Referenced by UI + Redirector.
 ├── Services/
 │   ├── ResolutionEngine.cs      tiered cascade + race + feedback loop
 │   ├── StrategyMemory.cs        per-host W/L ranking (version-gated)
 │   ├── RelayServer.cs           localhost.youtube.com trust-bypass proxy
 │   ├── HostsManager.cs          hosts-file setup + integrity checks
 │   ├── PatcherService.cs        hooks VRChat's yt-dlp.exe → Redirector
 │   ├── VrcLogMonitor.cs         tails VRChat output log; raises playback events
 │   ├── PotProviderService.cs    bgutil PO-token sidecar
 │   ├── BgutilPluginUpdater.cs   live updates the yt-dlp bgutil plugin
 │   ├── BrowserExtractService.cs Puppeteer-based JS-challenge bypass
 │   ├── WarpService.cs           optional Cloudflare WARP SOCKS5
 │   ├── CurlImpersonateClient.cs real-Chrome TLS fingerprint probe/fetch
 │   ├── Tier2WebSocketClient.cs  cloud resolver (WhyKnot.dev)
 │   └── …
 ├── Diagnostics/       SystemEventBus, SystemEvent, ErrorCodes
 ├── IPC/               HttpIpcServer, PipeServer, WebSocketIpcServer
 ├── Models/            AppConfig, HistoryEntry
 └── Logging/

WKVRCProxy.Redirector/  Tiny wrapper yt-dlp-replacement. VRChat's yt-dlp.exe
                        is patched to call this; it marshals the request into
                        WKVRCProxy's IPC and returns the resolved URL.

WKVRCProxy.HlsTests/    xUnit suite (124 tests).
WKVRCProxy.TestHarness/ Standalone scenario runner for local exploration.
```

### Request flow

```
VRChat world script → VRChat yt-dlp (patched) → Redirector.exe
                                                     │
                                                     ▼ (IPC)
                               ResolutionEngine
                               ├── cache hit? → return
                               ├── fast-path? → run remembered strategy
                               ├── else       → cold-race tier1 strategies
                               │                  (plain, PO, impersonate, browser,
                               │                   vrchat-UA, mweb, WARP, …)
                               │                + tier2 (cloud) in parallel
                               ├── stealthy pre-flight probe
                               └── relay-wrap if host not on VRChat allowlist
                                                     │
                                                     ▼
                               returns http://localhost.youtube.com:{port}/play?target=…
                                                     │
                                                     ▼
                               AVPro plays it via our RelayServer
                                                     │
                                                     ▼
             VrcLogMonitor watches for "[AVProVideo] Error: Loading failed"
                 → demote strategy + evict cache + publish StrategyDemoted
```

## Requirements

- **Windows 10/11 x64** (the WebView2 runtime must be installed; VRChat is Windows-only anyway).
- **VRChat** installed on the same machine.
- **.NET 10 SDK** (`dotnet --version` ≥ 10.0).
- **Node.js 20+** (for the Vue UI build).
- **Git** + **PowerShell 5.1+** (the build script uses both).
- **Administrator** access on first run — the hosts-file setup requires UAC.

## Building

```powershell
# From the repo root
powershell -ExecutionPolicy Bypass -File build.ps1
```

What the script does:

1. Vendors third-party tools into `vendor/` on first run, cached by version: `yt-dlp.exe`, `deno.exe`, `curl-impersonate-win.exe`, `streamlink`, `warp` (wgcf + wireproxy), and the compiled `bgutil-ytdlp-pot-provider.exe` sidecar. Subsequent builds only re-fetch when the pinned version in `vendor/versions.json` lags upstream.
2. Builds the Vue UI (`vite build`) into `src/WKVRCProxy.UI/wwwroot/`.
3. Publishes `WKVRCProxy.UI` (win-x64, self-contained) and `WKVRCProxy.Redirector` to `dist/`.
4. Stamps `dist/version.txt` and the embedded assembly version with `YYYY.M.D.N-HASH`.

Output: `dist/WKVRCProxy.exe` plus `dist/tools/` (vendored helpers).

> **Memory wipe on version change:** `strategy_memory.json` is wiped every time the version in `dist/version.txt` changes, so learned rankings from a previous build can't silently survive logic changes. Dev runs without a `version.txt` (i.e. `dotnet run` against source) are exempt so iteration isn't lossy.

## Developing

### UI (Vue + Tailwind)

```powershell
cd src/WKVRCProxy.UI/ui
npm install
npm run dev   # hot-reload dev server
```

The Vite dev server is only useful for pure UI work — it doesn't have the Photino bridge, so runtime config/logs/events don't flow. For full-stack work, edit Vue files and run `npm run build` then `dotnet run` on the UI project, or use `build.ps1` and launch `dist/WKVRCProxy.exe`.

### .NET

```powershell
# Build + debug-run the desktop app
dotnet build
dotnet run --project src/WKVRCProxy.UI

# Tests (xUnit, 124 tests)
dotnet test src/WKVRCProxy.HlsTests

# Standalone scenario runner
dotnet run --project src/WKVRCProxy.TestHarness -- <url>
```

Target framework is `net10.0` (Core + Redirector) / `net10.0-windows` (UI + HlsTests, because they pull in `SupportedOSPlatform("windows")`).

### Settings & state (local)

Runtime state lives next to the exe — for dev runs that's `src/WKVRCProxy.UI/bin/Debug/net10.0-windows/`, for release it's `dist/`.

| File | Purpose |
| --- | --- |
| `app_config.json` | User settings: preferred tier, resolution floor, `NativeAvProUaHosts`, `EnablePreflightProbe`, history, … |
| `strategy_memory.json` | Learned W/L per `host:stream-type` / strategy. Wiped on version change. |
| `proxy-rules.json` | Per-domain relay behavior: forwarded headers, UA overrides, PO-token injection, curl-impersonate toggle. |
| `wkvrcproxy_<timestamp>.log` | Session log with correlation IDs. |

## Running

1. **Launch WKVRCProxy before VRChat.** It will prompt (once) for UAC to add `127.0.0.1 localhost.youtube.com` to your hosts file. Decline and the trust-bypass won't work — any resolution that doesn't hit a VRChat-trusted host will fail at playback.
2. **Start VRChat.** WKVRCProxy patches VRChat's `yt-dlp.exe` on first run if `autoPatchOnStart` is on (default), swapping it for the Redirector.
3. **Play a video in-world.** Watch the Logs tab — you should see `Starting resolution for: …`, a tier decision, `URL relay-wrapped on port …`, and `Final Resolution [tier1] [vod] in Xms`.

### What to look for if playback fails

- **`Demoted` chips in the Logs view** — the feedback loop caught a strategy whose URL AVPro rejected. Red dots on History entries show which specific resolutions failed playback. The next request for that host will re-cascade.
- **`[WARNING] [Playback] AVPro rejected resolved URL from 'X'`** — explicit demotion, with the correlation ID so you can grep back.
- **`Relay wrap skipped for <host>`** at INFO level — only expected for hosts on the deny-list (`vr-m.net` by default) or on VRChat's trusted list. If you see it for a random CDN, that CDN slipped into the trusted-host table by mistake; file it.

Runtime knobs live under **Settings → Network**:
- **Pre-Flight URL Probe** — verify URLs before handoff (catches dead cloud URLs; adds up to 5s on cold resolve).
- **Direct-Connect Hosts** — comma-separated deny-list for hosts that need to see traffic coming directly from the in-game player.

## Contributing

1. Fork, branch off `main`.
2. `dotnet test` must pass; add tests for behavior changes.
3. Keep the rationale in comments where non-obvious — the `=== WHY WE WRAP ===` block in `ResolutionEngine.cs` is the template.
4. Run `powershell -File build.ps1` at least once before PR to confirm the full pipeline builds.

Code style follows the surrounding file. The project deliberately avoids heavy abstraction layers — a new feature usually means extending an existing service, not introducing a new one.

## License

[MIT](LICENSE).
