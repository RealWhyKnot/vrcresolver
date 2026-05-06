# Development

How to build WKVRCProxy from source and iterate. For *why* the build looks the way it does, see [[Build Pipeline]].

## Requirements

- **Windows 10/11 x64.**
- **.NET 10 SDK** (`dotnet --version` >= 10.0).
- **Git** + **PowerShell 5.1+** (the build script uses both).
- **Visual Studio Build Tools "Desktop development with C++" workload** -- required for the patched yt-dlp wrapper's AOT publish (the linker step calls MSVC's `link.exe`). Install via Visual Studio Installer if missing; CI on `windows-latest` already has it. Without this workload, `build.ps1` will fail at the `WKVRCProxy.YtDlp` publish step with a `link.exe not found` or similar error.

## Project layout

| Project | Output | Role |
| --- | --- | --- |
| `WKVRCProxy.Shared` | DLL | Wire DTOs (`Protocol.cs`), `WkvrcPaths`, `Logger`, `CrashHandler`, `LogUtil`, `ToolsDirSweeper` -- referenced by all four exes |
| `WKVRCProxy` | `WKVRCProxy.exe` (~80 MB) | Watchdog daemon -- patches yt-dlp.exe, named-pipe IPC server, persistent WS to mesh server, hosts ticker, heartbeat |
| `WKVRCProxy.Updater` | `WKVRCProxy.Updater.exe` (~80 MB) | Standalone update check + apply |
| `WKVRCProxy.Uninstaller` | `WKVRCProxy.Uninstaller.exe` (~80 MB) | Standalone teardown |
| `WKVRCProxy.YtDlp` | `dist/tools/yt-dlp.exe` (~3.27 MB) | Patched yt-dlp wrapper. **AOT-published**, native code, no embedded runtime. `<AssemblyName>yt-dlp</AssemblyName>` so it deploys as `yt-dlp.exe` directly |
| `WKVRCProxy.Tests` | xUnit DLL | 80+ tests covering protocol round-trip, atomic copy / SHA, hosts parser, sweeper, heartbeat formatting, etc. |

All target `net10.0`. The watchdog/Updater/Uninstaller publish self-contained single-file with R2R; the YtDlp wrapper publishes AOT (single native exe, no runtime needed).

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
dotnet test src/WKVRCProxy.Tests
```

`dotnet run` against source is the fastest dev loop for the watchdog. The watchdog looks for the patched yt-dlp at `<install>/tools/yt-dlp.exe` -- for iterating on watchdog plumbing (`MeshClient` / `LocalIpcServer` / `PatchManager`), build the wrapper once into `dist/tools/` via `build.ps1 -SkipZip` and then iterate the watchdog with `dotnet run`. The wrapper itself is invoked by VRChat as `Tools/yt-dlp.exe` with VRChat-specific argv shapes -- `dotnet run` against `src/WKVRCProxy.YtDlp` standalone won't be a useful dev loop.

## Runtime state in dev

| Path | Purpose |
| --- | --- |
| `%LOCALAPPDATA%Low\WKVRCProxy\` | State root -- all watchdog/wrapper state lives here (LocalLow because the wrapper inherits Low integrity from VRChat's Tools dir) |
| `%LOCALAPPDATA%Low\WKVRCProxy\clean_exit.flag` | Written on graceful watchdog shutdown so the next launch knows the previous run exited cleanly |
| `%LOCALAPPDATA%Low\WKVRCProxy\logs\watchdog-<utc>.log` | Rolling watchdog log (10 MiB, 7-day retention). Tees `Console.Out` AND `Console.Error` |
| `%LOCALAPPDATA%Low\WKVRCProxy\logs\yt-dlp-wrapper.log` | Per-invocation wrapper trace, appended on every yt-dlp.exe call |
| `%LOCALAPPDATA%Low\WKVRCProxy\crashes\crash-<component>-<utc>.log` | Unhandled-exception postmortems (paths + username redacted) |
| `%LOCALAPPDATA%Low\VRChat\VRChat\Tools\yt-dlp.exe` | The patched copy in place (== `tools/yt-dlp.exe` from `dist/`) |
| `%LOCALAPPDATA%Low\VRChat\VRChat\Tools\yt-dlp-og.exe` | VRChat's vanilla yt-dlp, preserved as fallback |
| `\\.\pipe\WKVRCProxy.resolve` | Named pipe with explicit Low-integrity SACL -- Low-integrity wrapper connects, Medium-integrity watchdog accepts |

Always go through `WkvrcPaths.StateRoot()` / `WkvrcPaths.LogsDir()` / `WkvrcPaths.CrashesDir()` -- never hardcode `Environment.SpecialFolder.LocalApplicationData`.

## Contributing

See [`CONTRIBUTING.md`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/CONTRIBUTING.md) and [[Engineering Standards]].

The wiki content lives at [`wiki/`](https://github.com/RealWhyKnot/WKVRCProxy/tree/main/wiki). Edit there -- the [`wiki-sync.yml`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/.github/workflows/wiki-sync.yml) workflow mirrors to the GitHub Wiki on push to `main`.
