# Quick Start

Get WKVRCProxy resolving videos for VRChat in about 60 seconds.

## Requirements

- **Windows 10/11 x64.** WebView2 runtime must be installed (it usually is by default).
- **VRChat** installed on the same machine.
- **Administrator** access on first run — adding the hosts-file entry requires UAC.

## Install

1. Download the latest `WKVRCProxy-<version>.zip` from [Releases](https://github.com/RealWhyKnot/WKVRCProxy/releases/latest).
2. Extract it anywhere — but **not into `Program Files`**. The in-app updater needs to swap files in the install dir without elevation, which Program Files doesn't allow. Your home folder, `C:\Tools\`, or anywhere outside `Program Files` is fine.
3. The bundled `updater.exe` and `uninstall.exe` live next to `WKVRCProxy.exe`. **Settings → Maintenance** has buttons for both.

## First launch

1. **Run `WKVRCProxy.exe` BEFORE VRChat.** The first launch prompts for UAC once to add `127.0.0.1 localhost.youtube.com` to your hosts file. This is the trust-list bypass — see [[Relay Server]] for why. If you decline, resolution still works (the YouTube anti-bot fixes don't depend on the relay), but URLs from non-allowlisted hosts will fail at playback whenever AVPro's trust list is in force (typically public worlds with **Allow Untrusted URLs** off).
2. **Start VRChat.** WKVRCProxy patches VRChat's `yt-dlp.exe` on first run if `autoPatchOnStart` is on (default), swapping it for the Redirector. The patch is reverted on graceful shutdown and on uninstall.
3. **Play a video in-world.** Watch the **Logs** tab in WKVRCProxy — you should see `Starting resolution for: <URL>`, a tier decision, `URL relay-wrapped on port <N>`, and `Final Resolution [tier1] [vod] in <X>ms`.

## What to look for if playback fails

- **`Demoted` chips in the Logs view** — the feedback loop caught a strategy whose URL AVPro rejected. Red dots on History entries show which specific resolutions failed playback. The next request for that host will re-cascade with the bad strategy demoted.
- **`[WARNING] [Playback] AVPro rejected resolved URL from 'X'`** — explicit demotion with the correlation ID so you can grep back through the log.
- **`Relay wrap skipped for <host>` at INFO level** — only expected for hosts on the deny-list (default `vr-m.net`) or on VRChat's trusted list. If you see it for a random CDN, that CDN slipped into the trusted-host table by mistake — file a bug.

Full failure-mode playbook: [[Troubleshooting]].

## Knobs you might actually use

- **Settings → Network → Pre-Flight URL Probe** — verify URLs are reachable before handoff. Catches dead cloud URLs early; adds up to 5 s on cold resolves.
- **Settings → Network → Direct-Connect Hosts** — chip-list of hosts (default `vr-m.net`) that need to see traffic from AVPro directly. URLs on these hosts skip both the relay and the resolution cascade.
- **Settings → Advanced → Individual Strategies** — toggle `tier1:warp+default`, `tier1:warp+vrchat-ua`, `tier1:browser-extract`, etc. The first WARP run starts a local WireGuard helper (`wgcf` registers an account once, then `wireproxy` exposes a SOCKS5 listener); subsequent invocations are zero-cost.
- **Settings → Maintenance** — manual update check, update apply, uninstall.

Full reference: [[Settings Reference]].

## Updating

WKVRCProxy polls GitHub for new releases periodically. When one lands you'll see a banner and a modal. **Settings → Maintenance** also has a manual **Check now** button. Clicking **Update now** spawns `updater.exe`, which closes the UI, downloads the new zip, verifies its SHA256, swaps the install dir atomically, and relaunches.

Full mechanism: [[Update and Uninstall]].

## Uninstalling

**Settings → Maintenance → Uninstall WKVRCProxy** (with confirm dialog) spawns `uninstall.exe`, which:

- Restores VRChat's original `yt-dlp.exe` from the Redirector backup.
- Deletes `%LOCALAPPDATA%\WKVRCProxy\` and the install dir.
- Leaves the hosts-file entry in place — it's idle and harmless without WKVRCProxy running, but you can remove `127.0.0.1 localhost.youtube.com` from `C:\Windows\System32\drivers\etc\hosts` manually if you'd like.

Settings (`app_config.json`, `strategy_memory.json`) live next to the exe and are deleted with the install dir.
