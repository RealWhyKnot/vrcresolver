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

1. **Run `WKVRCProxy.exe` before VRChat.** UAC prompts once to add `127.0.0.1 localhost.youtube.com` to your hosts file. This entry is the public-instance trust-list bypass mechanism (VRChat's AVPro allows `*.youtube.com` URLs unconditionally); declining the prompt leaves private/friends instances working but limits coverage in public worlds with "Allow Untrusted URLs" off. The watchdog re-checks the entry every minute and re-adds it if it disappears.
2. The watchdog backs up VRChat's `yt-dlp.exe` to `yt-dlp-og.exe` and swaps in the patched build. **If VRChat is already running and holding `yt-dlp.exe`, the watchdog waits for VRChat to exit before applying the patch.** Console prints `Patch applied. Watching for VRChat overwrites — Ctrl+C to quit.`
3. **Start VRChat.** Watch for `[mesh] connected` in the watchdog window. Play a video in-world.

## What you'll see

- `[mesh] connected` / `[mesh] disconnected — <reason>` / `[mesh] reconnect attempt N in M s` — server connection state. Disconnects fall back to vanilla yt-dlp transparently; you don't have to do anything.
- `[patch] yt-dlp.exe was overwritten — re-applied.` — VRChat replaced the patched file (usually during launch). The watchdog restores it within 3 seconds.
- `yt-dlp-og.exe was missing — restored from bundled fallback (vNNN).` — the fallback was restored from the bundled vanilla build so the patched yt-dlp's server-down fallback path stays functional.
- Per-resolve summary, two lines per video play, e.g.:
  ```
  [14:32:10]  -> youtube.com  (AVPro 1080p)
  [14:32:13]     OK resolved  3.0s
  ```
  A `[via lh-yt]` tag on the first line indicates the URL came in via the `localhost.youtube.com` trust-list bypass.
- `[hosts] tick: …` — the periodic hosts-entry verifier (silent unless state changes).
- `[heartbeat] up=… mesh=connected resolves=N reconnects=N` — every 30 minutes when nothing else has logged recently. Confirmation the daemon is still alive on long sessions.

## Where logs live

`%LOCALAPPDATA%Low\WKVRCProxy\logs\` (rolling, 10 MiB cap, 7-day retention):

- `watchdog-<utc>.log` — full watchdog output, one file per process start.
- `yt-dlp-wrapper.log` — per-invocation trace from the patched yt-dlp wrapper.

Crash dumps land at `%LOCALAPPDATA%Low\WKVRCProxy\crashes\crash-<component>-<utc>.log`. All paths are user-readable; safe to attach to bug reports (paths and usernames are redacted before write).

## When the watchdog isn't running

You aren't in a broken state. The patched `yt-dlp.exe` execs `yt-dlp-og.exe` (the vanilla copy preserved alongside it) for resolution and you get vanilla yt-dlp behaviour. Same as never installing this tool. Failure mode rundown: [[Troubleshooting]].

## Closing it down

`Ctrl+C` in the watchdog window. The watchdog restores VRChat's vanilla `yt-dlp.exe` from `yt-dlp-og.exe`, writes a clean-exit flag so the next launch knows we shut down cleanly, and exits within ~12 s.

To remove WKVRCProxy entirely, see [[Update and Uninstall]].
