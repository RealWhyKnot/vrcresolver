# WKVRCProxy Wiki

WKVRCProxy is a Windows desktop app that makes VRChat's in-world video players resolve and play URLs reliably — including sources VRChat's built-in resolver can't handle, URLs blocked by bot detection, and hosts that aren't on VRChat's trusted allowlist.

This wiki is the long-form documentation. The [README](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/README.md) is the quick-start; everything below goes deeper.

## How it works (90 seconds)

VRChat's in-game video players resolve URLs with a bundled `yt-dlp.exe` and play them through AVPro. Two things break that pipeline in practice:

1. **AVPro's trusted-URL list.** Only a narrow set of hosts (`*.youtube.com`, `*.vimeo.com`, `*.vrcdn.live`, etc.) are allowed. Anything else is silently rejected with `[AVProVideo] Error: Loading failed.`
2. **Anti-bot detection.** YouTube increasingly requires PO tokens, valid `visitor_data` binding, and a real browser TLS fingerprint. The vanilla yt-dlp that VRChat ships can't provide any of that.

WKVRCProxy sits in front of VRChat's resolver pipeline and fixes both problems. The [[Architecture]] page walks the request flow end-to-end; [[Resolution Cascade]] explains how strategies are picked and learned per-host; [[Relay Server]] explains the `localhost.youtube.com` trick that bypasses AVPro's allowlist.

## Read these first

- **[[Architecture]]** — the request flow and how the projects fit together
- **[[Resolution Cascade]]** — tiers, strategies, the parallel race, the playback-feedback demotion loop
- **[[Relay Server]]** — trust-bypass, hosts file, the AVPro UA deny-list, and why we wrap by default
- **[[Engineering Standards]]** — coding rules that future contributors MUST follow

## Reference

- **[[Settings Reference]]** — every field in `app_config.json` with what it does
- **[[Runtime State]]** — every file the app writes, where, and what wipes it
- **[[Build Pipeline]]** — `build.ps1` phase by phase, vendored binaries, version stamping
- **[[IPC and Redirector]]** — VRChat → patched yt-dlp → Redirector → IPC → Core
- **[[Update and Uninstall]]** — the standalone updater/uninstaller binaries and what they touch
- **[[Troubleshooting]]** — common failure modes and how to read the Logs view

## Background

- **[[AVPro vs Unity]]** — VRChat's two video players, what each supports, what fails where, encoding recipes. Useful context for anyone reasoning about why a given URL plays or doesn't.

## For new maintainers

If you're new to this repo, read [[Architecture]] then [[Resolution Cascade]] then [[Engineering Standards]]. The README is fine for orientation; the wiki has the load-bearing detail.
