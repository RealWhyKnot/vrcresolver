# WKVRCProxy Wiki

WKVRCProxy is a tiny Windows console daemon that swaps VRChat's bundled `yt-dlp.exe` for a patched build that asks our resolver server (whyknot.dev) for help, and falls back to the vanilla yt-dlp if anything is unreachable.

The [README](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/README.md) is the pitch; this wiki covers the operational details.

## How it works (60 seconds)

1. The watchdog (`WKVRCProxy.exe`) backs up VRChat's `yt-dlp.exe` to `yt-dlp-og.exe` and replaces it with a patched build.
2. The patched `yt-dlp.exe` connects to the watchdog over a local named pipe (`\\.\pipe\WKVRCProxy.resolve`) and asks for resolution.
3. The watchdog forwards the request through a persistent WebSocket to whyknot.dev, returns the resolved URL, and the patched binary hands it to AVPro.
4. **Fallback path**: if the server can't resolve, the watchdog isn't running, or the pipe isn't there, the patched `yt-dlp.exe` execs `yt-dlp-og.exe` and you get vanilla yt-dlp behaviour. The watchdog absent does not break VRChat.

The watchdog also pins `127.0.0.1 localhost.youtube.com` in your hosts file (load-bearing for public-instance support; idle when this tool isn't running) and periodically restores the patched `yt-dlp.exe` if VRChat overwrites it during launch.

## For users

- **[[Quick Start]]** — install, first launch, and what to do when something fails
- **[[Update and Uninstall]]** — how the bundled updater and uninstaller behave
- **[[Troubleshooting]]** — common failure modes

## For maintainers

- **[[Build Pipeline]]** — `build.ps1` phase by phase, version stamping, the bundled-fallback fetch
- **[[Development]]** — build from source, contribute
- **[[Engineering Standards]]** — coding rules contributors MUST follow
- **[[Changelog]]** — release history
