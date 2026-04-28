# WKVRCProxy Wiki

WKVRCProxy is a Windows desktop app that runs alongside VRChat and rebuilds the video-resolution pipeline so URLs that fail with `[AVProVideo] Error: Loading failed.` actually play. The main pain it solves is YouTube's escalating bot-detection: PO tokens, browser TLS fingerprints, and `visitor_data` binding — the vanilla yt-dlp shipped with VRChat can't keep up. WKVRCProxy runs several resolution methods in parallel, learns what worked per-host, and self-heals when a resolved URL fails to play.

The [README](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/README.md) is the pitch; this wiki is everything else.

## How it works (90 seconds)

VRChat's in-game video players resolve URLs with a bundled `yt-dlp.exe` and play them through AVPro Video. Two things break that:

1. **The resolver loses to anti-bot.** YouTube increasingly requires PO tokens, valid `visitor_data` binding, and a browser TLS fingerprint. The vanilla yt-dlp can't supply any of that, and resolution silently fails. This is the dominant pain — it bites every user, in every instance type.
2. **AVPro's trusted-URL list (when in force).** AVPro will only play URLs from a small allowlist (`*.youtube.com`, `*.vrcdn.live`, etc.) when the user hasn't enabled "Allow Untrusted URLs" in VRChat's comfort settings. Mostly relevant in public-world play; private and friends-only instances where the toggle is on don't see it.

WKVRCProxy sits in front of VRChat's resolver pipeline and fixes both. [[Architecture]] walks the request flow end-to-end; [[Resolution Cascade]] explains how strategies are picked, raced, and learned per-host; [[Relay Server]] explains the `localhost.youtube.com` trick that bypasses the trust list when needed.

## For users

- **[[Quick Start]]** — install, first launch, and what to do when something fails
- **[[Settings Reference]]** — every knob in `app_config.json`
- **[[Troubleshooting]]** — common failure modes and how to read the Logs view
- **[[Update and Uninstall]]** — what the updater and uninstaller touch

## For maintainers

- **[[Architecture]]** — request flow and how the projects fit together
- **[[Resolution Cascade]]** — tiers, strategies, the parallel race, the demotion loop
- **[[Relay Server]]** — trust-bypass, hosts file, the AVPro UA deny-list, and why we wrap by default
- **[[Engineering Standards]]** — coding rules contributors MUST follow
- **[[Development]]** — build from source, run tests, dev workflow
- **[[Build Pipeline]]** — `build.ps1` phase by phase, vendored binaries, version stamping
- **[[IPC and Redirector]]** — VRChat → patched yt-dlp → Redirector → IPC → Core
- **[[Runtime State]]** — every file the app writes, where, and what wipes it

## Background

- **[[AVPro vs Unity]]** — VRChat's two video players, what each supports, what fails where, encoding recipes

## For new maintainers

If you're new to this repo, read [[Architecture]] then [[Resolution Cascade]] then [[Engineering Standards]]. [[Development]] has the dev workflow you'll need.
