# WKVRCProxy

A Windows desktop app that makes VRChat's in-world video players resolve and play URLs reliably — including sources VRChat's built-in resolver can't handle, URLs blocked by bot detection, and hosts that aren't on VRChat's trusted allowlist.

> **Status: alpha.** It works, but expect rough edges. Run it alongside VRChat; shut it down if something misbehaves.

📚 **Long-form documentation lives in the [wiki](https://github.com/RealWhyKnot/WKVRCProxy/wiki).** This README is the quick-start.

---

## What it does

VRChat's in-game video players resolve URLs with a bundled `yt-dlp.exe` and play them through AVPro Video. Two things break that pipeline in practice:

1. **AVPro's trusted-URL list.** Only a narrow set of hosts (`*.youtube.com`, `*.vimeo.com`, `*.vrcdn.live`, etc.) are allowed. Anything else is silently rejected with `[AVProVideo] Error: Loading failed.`
2. **Anti-bot detection.** YouTube increasingly requires PO tokens, valid `visitor_data` binding, and a real browser TLS fingerprint. The vanilla yt-dlp that VRChat ships can't provide any of that.

WKVRCProxy sits in front of VRChat's resolver pipeline and fixes both problems:

- **Trust-bypass via local relay.** A hosts-file mapping `127.0.0.1 localhost.youtube.com` routes any URL we "wrap" through a local relay that AVPro sees as a trusted YouTube host. Our relay then fetches the real upstream and streams it back. Hosts already on AVPro's allowlist pass through pristine. A narrow deny-list handles hosts (like `vr-m.net`) that need to see traffic coming directly from AVPro.

- **Tiered resolution cascade.** Multiple strategies (vanilla yt-dlp, PO-token via the bgutil sidecar, Chrome-TLS fingerprinting via `curl-impersonate`, a UnityPlayer UA path, mobile clients, headless-browser JS-challenge bypass, Cloudflare WARP egress, and a cloud resolver at `whyknot.dev`) race in parallel for each URL. Per-host **strategy memory** remembers what worked and skips the race next time.

- **Playback-feedback loop.** A log monitor watches VRChat's output for `[AVProVideo] Error: Loading failed`. When that fires, the responsible strategy is demoted, the cache entry is evicted, and the next request re-cascades. Prevents the engine from locking onto a strategy that returns URLs that don't actually play.

- **Pre-flight probe.** Verifies each resolved URL is reachable (using AVPro-shaped headers) before handing back to VRChat. Catches dead cloud URLs early.

➡ Full request flow: [[Architecture]] · How strategies are picked: [[Resolution Cascade]] · Why the relay is the default: [[Relay Server]]

---

## Requirements

- **Windows 10/11 x64.** WebView2 runtime must be installed (it usually is). VRChat is Windows-only anyway.
- **VRChat** installed on the same machine.
- **.NET 10 SDK** (`dotnet --version` ≥ 10.0).
- **Node.js 20+** (only needed if you build from source — for the Vue UI build).
- **Git** + **PowerShell 5.1+** (the build script uses both).
- **Administrator** access on first run — the hosts-file setup requires UAC.

## Install (release build)

Download the latest `WKVRCProxy-*.zip` from [Releases](https://github.com/RealWhyKnot/WKVRCProxy/releases), extract anywhere (avoid `Program Files` so the in-app updater can swap files without elevation), and run `WKVRCProxy.exe`.

The bundled `updater.exe` and `uninstall.exe` live next to it. **Settings → Maintenance** has buttons for both.

## Build from source

```powershell
# From the repo root
powershell -ExecutionPolicy Bypass -File build.ps1
```

What the script does (full breakdown in [[Build Pipeline]]):

1. Vendors third-party tools into `vendor/` on first run, cached by version: `yt-dlp.exe`, `deno.exe`, `curl-impersonate-win.exe`, `streamlink`, `wgcf` + `wireproxy` for WARP, and the compiled `bgutil-ytdlp-pot-provider.exe` PO-token sidecar. Subsequent builds skip the network entirely for any binary whose pinned version in `vendor/versions.json` matches upstream and whose vendor file still exists.
2. Builds the Vue UI (`vite build`) into `src/WKVRCProxy.UI/wwwroot/`.
3. Publishes `WKVRCProxy.UI`, `WKVRCProxy.Redirector`, `WKVRCProxy.Updater`, and `WKVRCProxy.Uninstaller` (all win-x64, self-contained, single-file) to `dist/`.
4. Stamps `dist/version.txt` and the embedded assembly version with `YYYY.M.D.N-HASH`.
5. Produces `release/WKVRCProxy-<version>.zip` plus a SHA256 (used by the tag-driven release workflow and verified by `updater.exe`).

> **Memory wipe on version change:** `strategy_memory.json` is wiped every time `dist/version.txt` changes, so learned rankings from a previous build can't silently survive logic changes. `dotnet run` against source is exempt so iteration isn't lossy.

## Develop

### UI (Vue 3 + Tailwind)

```powershell
cd src/WKVRCProxy.UI/ui
npm install
npm run dev   # hot-reload dev server on http://localhost:5173
```

The Vite dev server is only useful for pure UI work — it has no Photino bridge, so runtime config / logs / events don't flow. For full-stack iteration: edit Vue files, then `npm run build` and `dotnet run` on the UI project, or run `build.ps1` and launch `dist/WKVRCProxy.exe`.

### .NET

```powershell
# Build + debug-run the desktop app
dotnet build
dotnet run --project src/WKVRCProxy.UI

# Tests (xUnit, 131 tests). HlsTests is intentionally not in the slnx, so target it directly.
dotnet test src/WKVRCProxy.HlsTests

# Standalone scenario runner (resolves a URL from the CLI without VRChat)
dotnet run --project src/WKVRCProxy.TestHarness -- <url>
```

Target framework is `net10.0` (Core + Redirector + TestHarness) / `net10.0-windows` (UI + HlsTests + Updater + Uninstaller).

### Settings & runtime state

Lives next to the exe — for dev, that's `src/WKVRCProxy.UI/bin/Debug/net10.0-windows/`. Full inventory in [[Runtime State]].

Highlights:

| File | Purpose |
| --- | --- |
| `app_config.json` | User settings — every field documented in [[Settings Reference]] |
| `strategy_memory.json` | Per-host W/L per strategy. **Wiped on version change.** |
| `proxy-rules.json` | Per-domain relay behaviour: forwarded headers, UA overrides, PO-token injection |
| `wkvrcproxy_<timestamp>.log` | Session log with correlation IDs |
| `%LOCALAPPDATA%\WKVRCProxy\ipc_port.dat` | Dynamic port the Redirector connects to |

## Run

1. **Launch WKVRCProxy before VRChat.** First run prompts for UAC to add `127.0.0.1 localhost.youtube.com` to your hosts file. Decline and the trust-bypass won't work — any resolution that doesn't hit a VRChat-trusted host will fail at playback.
2. **Start VRChat.** WKVRCProxy patches VRChat's `yt-dlp.exe` on first run if `autoPatchOnStart` is on (default), swapping it for the Redirector. The patch is reverted on graceful shutdown and on uninstall.
3. **Play a video in-world.** Watch the **Logs** tab — you should see `Starting resolution for: …`, a tier decision, `URL relay-wrapped on port …`, and `Final Resolution [tier1] [vod] in Xms`.

### What to look for if playback fails

- **`Demoted` chips in the Logs view** — the feedback loop caught a strategy whose URL AVPro rejected. Red dots on History entries show which specific resolutions failed playback. The next request for that host will re-cascade.
- **`[WARNING] [Playback] AVPro rejected resolved URL from 'X'`** — explicit demotion with the correlation ID so you can grep back.
- **`Relay wrap skipped for <host>` at INFO level** — only expected for hosts on the deny-list (`vr-m.net` by default) or on VRChat's trusted list. If you see it for a random CDN, that CDN slipped into the trusted-host table by mistake — file it.

Full troubleshooting playbook: [[Troubleshooting]].

### Knobs you might actually use

- **Settings → Network → Pre-Flight URL Probe** — verify URLs before handoff (catches dead cloud URLs; adds up to 5 s on cold resolves).
- **Settings → Network → Direct-Connect Hosts** — chip-list of hosts (default `vr-m.net`) that need to see traffic from AVPro directly. URLs on these hosts skip both the relay and the resolution cascade.
- **Settings → Advanced → Individual Strategies** — toggle `tier1:warp+default`, `tier1:warp+vrchat-ua`, `tier1:browser-extract`, etc. The first WARP run starts a local WireGuard helper (`wgcf` registers an account once, then `wireproxy` exposes a SOCKS5 listener); subsequent invocations are zero-cost.
- **Settings → Maintenance** — manual update check, update apply, uninstall.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the short version, and [[Engineering Standards]] for the load-bearing rules (no C# string interpolation in stamped files, raw-bytes stdout in the Redirector, wrap-by-default, etc.). The wiki content itself lives at [`docs/wiki/`](docs/wiki/) — edit there, the [`wiki-sync.yml`](.github/workflows/wiki-sync.yml) workflow mirrors to the GitHub Wiki on push to `main`.

## Reporting bugs

Use the [bug report template](https://github.com/RealWhyKnot/WKVRCProxy/issues/new?template=bug_report.yml). Paste the correlation-ID block from the Logs view verbatim — that's the difference between "we'll figure it out" and "we can't help from here."

For security issues, use the private [Security Advisories](https://github.com/RealWhyKnot/WKVRCProxy/security/advisories/new) flow. Don't open a public issue.

## License

[MIT](LICENSE).
