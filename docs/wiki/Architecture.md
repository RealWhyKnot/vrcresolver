# Architecture

WKVRCProxy is five .NET projects plus a Vue 3 frontend.

## Projects

| Project | Target | Role |
|---|---|---|
| `WKVRCProxy.Core` | `net10.0` | Engine library. Resolution cascade, relay server, hosts manager, IPC servers, strategy memory, log monitor, patcher service, update checker, all helpers. Referenced by every other project. |
| `WKVRCProxy.UI` | `net10.0-windows` | Photino (WebView2) desktop shell + Vue 3/TypeScript/Tailwind/Pinia frontend in `ui/`. `Program.cs` bridges Vue webmessages to Core's `SystemEventBus`. |
| `WKVRCProxy.Redirector` | `net10.0` | Tiny single-file console exe that **replaces** VRChat's `yt-dlp.exe`. Connects to Core via IPC, returns the resolved URL on stdout. |
| `WKVRCProxy.Updater` | `net10.0-windows` | Standalone `updater.exe`. Waits for the UI to exit, downloads the latest release zip, verifies SHA256, atomic-swaps the install dir, relaunches. |
| `WKVRCProxy.Uninstaller` | `net10.0-windows` | Standalone `uninstall.exe`. Restores VRChat's original `yt-dlp.exe`, wipes `%LOCALAPPDATA%\WKVRCProxy\`, schedules install-dir delete. |
| `WKVRCProxy.HlsTests` | `net10.0-windows` | xUnit suite. **Excluded from the slnx** so the solution build doesn't pull it. CI targets it directly. |
| `WKVRCProxy.TestHarness` | `net10.0` | Standalone scenario runner. Takes a URL, runs the cascade interactively. Excluded from the slnx. |

The `WKVRCProxy.slnx` solution file deliberately lists only the five "shipping" projects. `dotnet build` from the repo root therefore won't touch HlsTests or TestHarness — that's why CI restores `src/WKVRCProxy.HlsTests` directly. Don't add HlsTests to the slnx unless you want the test project to ride along on every full-solution build.

## Request flow

```
VRChat world script
  └─ VRChat.exe
      └─ yt-dlp.exe (patched: this is now Redirector.exe)
          └─ IPC → ws://127.0.0.1:{ipc_port}/
              └─ HttpIpcServer / WebSocketIpcServer
                  └─ ResolutionEngine
                      ├─ cache hit?            → return cached
                      ├─ fast-path? (memory)   → run remembered strategy
                      └─ cold-race
                          ├─ tier1 strategies (parallel waves)
                          ├─ tier2:cloud-whyknot (parallel)
                          ├─ tier3:yt-dlp-og (fallback)
                          └─ tier4:passthrough (always-return-something)
                  └─ pre-flight probe (CheckUrlReachable)
                  └─ relay-wrap (ApplyRelayWrap)
              ← returns http://localhost.youtube.com:{relay_port}/play?target=…
          ← stdout (raw bytes, trailing \n)
      ← VRChat hands to AVPro
          └─ AVPro plays via RelayServer
              └─ VrcLogMonitor watches output log
                  └─ "[AVProVideo] Error: Loading failed" → demote strategy + evict cache
```

See [[Resolution Cascade]] for the tier/strategy detail, [[Relay Server]] for the wrap and trust-bypass, [[IPC and Redirector]] for the IPC contract.

## Key files

| Concern | File |
|---|---|
| Tiered cascade + race + feedback loop | [`src/WKVRCProxy.Core/Services/ResolutionEngine.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/ResolutionEngine.cs) |
| Per-host W/L learning | [`src/WKVRCProxy.Core/Services/StrategyMemory.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/StrategyMemory.cs) |
| Trust-bypass relay | [`src/WKVRCProxy.Core/Services/RelayServer.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/RelayServer.cs) |
| Hosts-file management | [`src/WKVRCProxy.Core/Services/HostsManager.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/HostsManager.cs) |
| VRChat yt-dlp patching | [`src/WKVRCProxy.Core/Services/PatcherService.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/PatcherService.cs) |
| Tools-dir discovery (also used by Uninstaller) | [`src/WKVRCProxy.Core/VrcPathLocator.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/VrcPathLocator.cs) |
| AVPro log tail + playback events | [`src/WKVRCProxy.Core/Services/VrcLogMonitor.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/VrcLogMonitor.cs) |
| PO-token sidecar | [`src/WKVRCProxy.Core/Services/PotProviderService.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/PotProviderService.cs) |
| Bgutil plugin self-update | [`src/WKVRCProxy.Core/Services/BgutilPluginUpdater.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/BgutilPluginUpdater.cs) |
| curl-impersonate wrapper | [`src/WKVRCProxy.Core/Services/CurlImpersonateClient.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/CurlImpersonateClient.cs) |
| Headless-browser JS-challenge bypass | [`src/WKVRCProxy.Core/Services/BrowserExtractService.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/BrowserExtractService.cs) |
| Cloudflare WARP SOCKS5 | [`src/WKVRCProxy.Core/Services/WarpService.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/WarpService.cs) |
| Cloud resolver | [`src/WKVRCProxy.Core/Services/Tier2WebSocketClient.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/Tier2WebSocketClient.cs) |
| Update poller | [`src/WKVRCProxy.Core/Services/AppUpdateChecker.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/AppUpdateChecker.cs) |
| User settings model | [`src/WKVRCProxy.Core/Models/AppConfig.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Models/AppConfig.cs) |

## Why this shape

- **Core is a library**, not a service. UI, Redirector, Updater, Uninstaller all reference it. Single source of truth for `VrcPathLocator`, `PatcherService.RestoreYtDlpInTools()`, version-comparison logic.
- **Redirector is intentionally tiny** (~5 KB). VRChat's yt-dlp hook calls it like a normal yt-dlp; if Core isn't running, the redirector falls back to returning the original URL so VRChat's native resolver still gets a shot.
- **Updater + Uninstaller are standalone single-file exes**. They run when WKVRCProxy isn't, so they can't share its IPC. Each loads what it needs from Core directly.
- **Vue UI talks to Core via Photino webmessage IPC**, not over HTTP. The `HttpIpcServer` is for the *Redirector*, not for the UI.
