# Development

How to build WKVRCProxy from source and iterate. For *why* the build looks the way it does, see [[Build Pipeline]].

## Requirements

- **Windows 10/11 x64.**
- **.NET 10 SDK** (`dotnet --version` ≥ 10.0).
- **Git** + **PowerShell 5.1+** (the build script uses both).

## Project layout

| Project | Output | Role |
| --- | --- | --- |
| `WKVRCProxy` | `WKVRCProxy.exe` | The watchdog daemon — patches yt-dlp.exe, holds the WS to the mesh server, runs the local pipe |
| `WKVRCProxy.Updater` | `WKVRCProxy.Updater.exe` | Standalone update check + apply |
| `WKVRCProxy.Uninstaller` | `WKVRCProxy.Uninstaller.exe` | Standalone teardown |
| `WKVRCProxy.Shared` | DLL | DTOs and constants shared across the three exes |

All four target `net10.0`. Single-file self-contained publish at release time.

## Build a release zip

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
# or, faster local rebuilds:
powershell -ExecutionPolicy Bypass -File build.ps1 -SkipZip
```

Output: `dist/` (the layout that goes into the zip) and `release/WKVRCProxy-v<version>.zip`. Phase-by-phase breakdown: [[Build Pipeline]].

## .NET iteration

```powershell
dotnet build WKVRCProxy.slnx
dotnet run --project src/WKVRCProxy
```

`dotnet run` against source is the fastest dev loop. The watchdog will look for the patched yt-dlp at `<install>/tools/yt-dlp-patched.exe` — drop a local build of the patched yt-dlp project there or bypass `PatchManager.Start()` while iterating on `MeshClient` / `LocalIpcServer` plumbing.

## Runtime state in dev

| Path | Purpose |
| --- | --- |
| `%LOCALAPPDATA%\WKVRCProxy\clean_exit.flag` | Written on graceful shutdown so the next launch knows the previous run exited cleanly |
| `%LOCALAPPDATA%Low\VRChat\VRChat\Tools\yt-dlp.exe` | The patched copy in place (== `tools/yt-dlp-patched.exe` from this install) |
| `%LOCALAPPDATA%Low\VRChat\VRChat\Tools\yt-dlp-og.exe` | VRChat's vanilla yt-dlp, preserved as fallback |
| `\\.\pipe\WKVRCProxy.resolve` | Named pipe the patched yt-dlp connects to |

## Contributing

See [`CONTRIBUTING.md`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/CONTRIBUTING.md) and [[Engineering Standards]].

The wiki content lives at [`wiki/`](https://github.com/RealWhyKnot/WKVRCProxy/tree/main/wiki). Edit there — the [`wiki-sync.yml`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/.github/workflows/wiki-sync.yml) workflow mirrors to the GitHub Wiki on push to `main`.
