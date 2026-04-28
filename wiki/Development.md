# Development

How to build WKVRCProxy from source and iterate. For *why* the build looks the way it does (vendoring, version stamping, the strategy-memory wipe rule, etc.), see [[Build Pipeline]].

## Requirements

- **Windows 10/11 x64.**
- **.NET 10 SDK** (`dotnet --version` ≥ 10.0).
- **Node.js 20+** for the Vue UI build.
- **Git** + **PowerShell 5.1+** (the build script uses both).

## Build a release

```powershell
# From the repo root
powershell -ExecutionPolicy Bypass -File build.ps1
```

The script:

1. Vendors third-party tools into `vendor/` on first run, cached by version: `yt-dlp.exe`, `deno.exe`, `curl-impersonate-win.exe`, `streamlink`, `wgcf` + `wireproxy`, and the compiled `bgutil-ytdlp-pot-provider.exe` PO-token sidecar. Subsequent builds skip the network entirely if `vendor/versions.json` matches and the file exists.
2. Builds the Vue UI (`vite build`) into `src/WKVRCProxy.UI/wwwroot/`.
3. Publishes `WKVRCProxy.UI`, `WKVRCProxy.Redirector`, `WKVRCProxy.Updater`, and `WKVRCProxy.Uninstaller` (all win-x64, self-contained, single-file) to `dist/`.
4. Stamps `dist/version.txt` and the embedded assembly version with `YYYY.M.D.N-HASH`.
5. Produces `release/WKVRCProxy-<version>.zip` plus a SHA256 (used by the tag-driven release workflow and verified by `updater.exe`).

> **Memory wipe on version change:** `strategy_memory.json` is wiped every time `dist/version.txt` changes, so learned rankings from a previous build can't silently survive logic changes. `dotnet run` against source is exempt so iteration isn't lossy.

Phase-by-phase breakdown: [[Build Pipeline]].

## UI (Vue 3 + Tailwind)

```powershell
cd src/WKVRCProxy.UI/ui
npm install
npm run dev   # hot-reload dev server on http://localhost:5173
```

The Vite dev server is only useful for pure UI work — it has no Photino bridge, so runtime config / logs / events don't flow. For full-stack iteration: edit Vue files, then `npm run build` and `dotnet run` on the UI project, or run `build.ps1` and launch `dist/WKVRCProxy.exe`.

## .NET

```powershell
# Build + debug-run the desktop app
dotnet build
dotnet run --project src/WKVRCProxy.UI

# Tests (xUnit). HlsTests is intentionally not in the slnx, so target it directly.
dotnet test src/WKVRCProxy.HlsTests

# Standalone scenario runner (resolves a URL from the CLI without VRChat)
dotnet run --project src/WKVRCProxy.TestHarness -- <url>
```

Target framework is `net10.0` (Core + Redirector + TestHarness) / `net10.0-windows` (UI + HlsTests + Updater + Uninstaller).

## Settings & runtime state in dev

Lives next to the exe — for `dotnet run`, that's `src/WKVRCProxy.UI/bin/Debug/net10.0-windows/`. Highlights:

| File | Purpose |
| --- | --- |
| `app_config.json` | User settings — every field documented in [[Settings Reference]] |
| `strategy_memory.json` | Per-host W/L per strategy. **Wiped on version change.** |
| `proxy-rules.json` | Per-domain relay behaviour: forwarded headers, UA overrides, PO-token injection |
| `wkvrcproxy_<timestamp>.log` | Session log with correlation IDs |
| `%LOCALAPPDATA%\WKVRCProxy\ipc_port.dat` | Dynamic port the Redirector connects to |

Full inventory: [[Runtime State]].

## Contributing

See [`CONTRIBUTING.md`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/CONTRIBUTING.md) for the short version and [[Engineering Standards]] for the load-bearing rules (no C# string interpolation in stamped files, raw-bytes stdout in the Redirector, wrap-by-default, etc.).

The wiki content lives at [`wiki/`](https://github.com/RealWhyKnot/WKVRCProxy/tree/main/wiki). Edit there — the [`wiki-sync.yml`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/.github/workflows/wiki-sync.yml) workflow mirrors to the GitHub Wiki on push to `main`.
