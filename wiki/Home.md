# WKVRCProxy Wiki

The [README](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/README.md) is the pitch. This wiki is the manual.

## What it actually does

VRChat's video players invoke `Tools/yt-dlp.exe` per playback request. Vanilla yt-dlp works but it's slow on cold starts, breaks against YouTube's anti-bot every few weeks, and hands AVPro stream URLs that VRChat's trusted-host allowlist rejects in default-public worlds. WKVRCProxy intercepts that invocation and routes resolution through a remote backend behind Cloudflare WARP, then wraps the resulting URL through a local listener so AVPro accepts it everywhere.

```
+-----------------+        +------------------+        +-----------------+
| VRChat (AVPro)  |  pipe  | WKVRCProxy.exe   |   WS   | whyknot.dev     |
| Tools/yt-dlp.exe| -----> | (the watchdog)   | -----> | /mesh resolver  |
+-----------------+        +--------+---------+        +-----------------+
                                    |
                                    |  resolved URL wrapped through
                                    v  http://localhost.youtube.com:{port}/play?target=...
                           +------------------+
                           | Local listener   |  proxies bytes back to AVPro
                           | 127.0.0.1:{port} |  AVPro's trust list passes
                           +------------------+

If anything in the chain breaks, the patched shim execs the preserved
vanilla yt-dlp at Tools/yt-dlp-og.exe and AVPro plays whatever vanilla
yt-dlp returns. Watchdog absent does not break VRChat.
```

## Component map

- `WKVRCProxy.exe` -- the long-running watchdog. Holds the WebSocket, runs the named-pipe IPC server, runs the trust-gateway HTTP listener, ticks the patch state every 3 seconds, ticks the hosts entry every 60 seconds.
- `tools/yt-dlp.exe` -- the patched shim that VRChat invokes. Replaces VRChat's bundled yt-dlp at install time. AOT-compiled, 3.27 MB native code.
- `tools/yt-dlp-og.exe` -- the vanilla yt-dlp the patcher preserved at install time. Used as the fallback path on every failure.
- `tools/yt-dlp-og-fallback.exe` -- a bundled vanilla yt-dlp that ships in the dist. Safety net for the case where the user's `yt-dlp-og.exe` goes missing (file deleted, fresh install, etc.) -- the watchdog drops a known-good copy back into VRChat's Tools dir on the next 3-second tick.
- `WKVRCProxy.Updater.exe` -- self-update against GitHub releases. SHA-256 verified, atomic file swap, 15-second prompt timeout. Manual run.
- `WKVRCProxy.Uninstaller.exe` -- step-based uninstall with per-step breadcrumbs. Restores vanilla `yt-dlp.exe`, removes the hosts entry (UAC re-exec only if the entry is actually present), wipes `%LOCALAPPDATA%Low\WKVRCProxy\`, deletes its own install directory.

## State on disk

| Path | What lives there |
|---|---|
| `%LOCALAPPDATA%Low\WKVRCProxy\` | All persistent state. LocalLow because the wrapper inherits Low integrity from VRChat's Tools dir and needs read+write access. |
| `%LOCALAPPDATA%Low\WKVRCProxy\logs\` | Per-session watchdog logs + per-invocation wrapper traces. |
| `%LOCALAPPDATA%Low\WKVRCProxy\v3_welcome_cache.json` | Per-node welcome handshake fingerprints. |
| `%LOCALAPPDATA%Low\WKVRCProxy\resolve_cache.json` | Per-(URL, player, format, node) resolved-frame cache. |
| `%LOCALAPPDATA%Low\WKVRCProxy\client_id.txt` | Persistent client ID across runs. |
| `%LOCALAPPDATA%Low\WKVRCProxy\codec-state.json` | Tracking for the AV1/HEVC/VP9 codec installer. |
| `%LOCALAPPDATA%Low\WKVRCProxy\relay_port.txt` | Trust-gateway listener port; written by watchdog, read by wrapper. |
| `%LOCALAPPDATA%Low\WKVRCProxy\crashes\` | Crash logs from the unhandled-exception handlers. |

## Pages

- [[Quick Start]] -- install walkthrough, the UAC prompts to expect, what success looks like.
- [[Trust Gateway]] -- the localhost.youtube.com mechanism, why it exists, what the request flow looks like, the parked HTTPS path.
- [[Mesh Protocol]] -- v3 wire details, msgpack negotiation, welcome cache, fallback paths.
- [[Update and Uninstall]] -- how the bundled updater and uninstaller actually behave.
- [[Troubleshooting]] -- log-line greps for the failure modes you'll hit.
- [[Logs and Diagnostics]] -- where every log file lives and what to attach to a bug report.
- [[Engineering Standards]] -- contributor rules.
- [[Build Pipeline]] -- `build.ps1` phase by phase, version stamping, the bundled-fallback fetch.
- [[Development]] -- build from source, run the test suite.
- [[Release Notes]] -- the running release history.
- [[Changelog]] -- auto-maintained per-commit log.
