# Quick Start

Get WKVRCProxy resolving videos for VRChat in about 60 seconds.

## Requirements

- **Windows 10/11 x64.**
- **VRChat** installed on the same machine, and **launched at least once** so it has dropped its bundled `yt-dlp.exe` into `%LOCALAPPDATA%Low\VRChat\VRChat\Tools\`. The watchdog needs to preserve that file as the fallback.
- **Administrator** access on first run — adding the hosts-file entry requires one UAC prompt.

## Install

1. Download the latest `WKVRCProxy-<version>.zip` from [Releases](https://github.com/RealWhyKnot/WKVRCProxy/releases/latest).
2. Extract anywhere except `Program Files` — the bundled updater can't swap files there without elevation. Your home folder or `C:\Tools\` is fine.
3. The folder contains `WKVRCProxy.exe`, `WKVRCProxy.Updater.exe`, `WKVRCProxy.Uninstaller.exe`, and a `tools/` subdir with the patched yt-dlp build and a vanilla yt-dlp fallback.

## First launch

1. **Run `WKVRCProxy.exe` before VRChat.** UAC prompts once to add `127.0.0.1 localhost.youtube.com` to your hosts file. This is load-bearing for public-instance support — declining it leaves private/friends instances working but breaks playback on URLs that aren't on AVPro's trusted-host allowlist in public worlds.
2. The watchdog backs up VRChat's `yt-dlp.exe` to `yt-dlp-og.exe` and swaps in the patched build. Console prints `Patch applied. Watching for VRChat overwrites — Ctrl+C to quit.`
3. **Start VRChat.** Watch for `[mesh] connected` in the watchdog window. Play a video in-world.

## What you'll see

- `[mesh] connected` / `[mesh] disconnected — <reason>` / `[mesh] reconnect attempt N in M s` — server connection state. Disconnects fall back to vanilla yt-dlp transparently; you don't have to do anything.
- `[patch] yt-dlp.exe was overwritten — re-applied.` — VRChat replaced the patched file (usually during launch). The watchdog restores it within 3 seconds.
- `yt-dlp-og.exe was missing — restored from bundled fallback (vNNN).` — the fallback was restored from the bundled vanilla build so the patched yt-dlp's server-down fallback path stays functional.

## When the watchdog isn't running

You aren't in a broken state. The patched `yt-dlp.exe` execs `yt-dlp-og.exe` (the vanilla copy preserved alongside it) for resolution and you get vanilla yt-dlp behaviour. Same as never installing this tool. Failure mode rundown: [[Troubleshooting]].

## Closing it down

`Ctrl+C` in the watchdog window. The watchdog restores VRChat's vanilla `yt-dlp.exe` from `yt-dlp-og.exe`, writes a clean-exit flag so the next launch knows we shut down cleanly, and exits within ~12 s.

To remove WKVRCProxy entirely, see [[Update and Uninstall]].
