# WKVRCProxy

VRChat's in-world video players use yt-dlp under the hood. Vanilla yt-dlp is slow on cold starts, breaks against YouTube changes the moment they ship, and hands AVPro URLs that VRChat's trusted-host allowlist rejects in default-public worlds. WKVRCProxy fixes all three.

A Windows console daemon swaps VRChat's `Tools/yt-dlp.exe` for a patched build that asks a remote resolver (whyknot.dev) and routes the resulting stream URL through a local listener so AVPro accepts it everywhere. If the remote is unreachable or anything else goes wrong, the patched binary falls through to the vanilla yt-dlp it preserved at install time. Watchdog absent does not break VRChat.

> **Status: alpha.** Works in production. Run it alongside VRChat; close it when done.

**[Latest release](https://github.com/RealWhyKnot/WKVRCProxy/releases/latest)** | **[Wiki](https://github.com/RealWhyKnot/WKVRCProxy/wiki)** | **[Report a bug](https://github.com/RealWhyKnot/WKVRCProxy/issues/new?template=bug_report.yml)**

---

## What you get

- **Public-instance YouTube playback.** A local HTTP listener at `localhost.youtube.com:{port}` wraps every resolved stream URL so VRChat's AVPro trust list accepts it in default-public worlds. The trust gateway is HTTP-only today; HTTPS is parked behind concrete trigger conditions, see the wiki.
- **Server-side resolution behind WARP egress.** YouTube extraction happens on the backend behind Cloudflare WARP, so regional blocks and IP-rate-limits don't cost you a stalled video.
- **Per-URL disk cache.** First resolve of a URL takes 2 to 3 seconds end-to-end. Second resolve in the same session lands in 20 ms (a 99.2% latency reduction on the wrapper-side). Cache survives watchdog restarts.
- **HLS-first format selection.** AVPro gets 1080p HLS, not 360p progressive mp4. The dispatcher picks the best match for the player VRChat asked for.
- **AOT-compiled.** Watchdog binary is 10.1 MB on disk; cold start to mesh-connected is ~486 ms. The patched yt-dlp shim VRChat invokes per video player is 3.3 MB native code with no JIT.
- **Server auto-updates yt-dlp nightly.** YouTube ships a breaking change at 2 AM UTC; the resolver picks up the upstream fix within 24 hours without you doing anything.
- **Graceful fallback.** Every failure path execs the vanilla `yt-dlp.exe` that was bundled into VRChat. You never end up with a broken `Tools/yt-dlp.exe`.

What it does NOT do: bypass DRM, change VRChat's per-avatar limits, host content, accept your YouTube login (server tier may use cookies; client tier never does), or work without internet (the fallback path is vanilla yt-dlp; the resolver path needs whyknot.dev reachable).

---

## What's in the dist

| Binary | Role |
|---|---|
| `WKVRCProxy.exe` | the watchdog. Long-running console window. Patches VRChat, holds the WebSocket, runs the trust-gateway listener. |
| `tools/yt-dlp.exe` | the patched shim. Replaces VRChat's bundled yt-dlp at install time. AOT, 3.3 MB. |
| `tools/yt-dlp-og-fallback.exe` | bundled vanilla yt-dlp. Safety net when VRChat's own copy is missing (fresh install, file deleted, etc.). |
| `WKVRCProxy.Updater.exe` | self-update against GitHub releases. Manual run. |
| `WKVRCProxy.Uninstaller.exe` | restore the original `yt-dlp.exe`, remove the hosts entry, wipe state. No prompt; running it IS consent. |

Target: Windows 10/11 x64. Single-file, self-contained .NET 10. No installer, no admin install dir.

---

## Quick start

1. **Launch VRChat once first.** The patcher needs VRChat's own `yt-dlp.exe` in `Tools/` so it can preserve the original as `yt-dlp-og.exe`.
2. **Download** `WKVRCProxy-*.zip` from the latest release.
3. **Extract anywhere except `Program Files`.** The bundled updater swaps files in place and can't write to `Program Files` without elevation.
4. **Run `WKVRCProxy.exe`.** UAC prompts once to add `127.0.0.1 localhost.youtube.com` to your hosts file. That entry is load-bearing for public-instance support and idle when the watchdog isn't running.
5. **Launch VRChat.** The watchdog window prints `[mesh] connected` once the WebSocket is up. Paste a YouTube URL in any in-world video player.

To uninstall: run `WKVRCProxy.Uninstaller.exe` from the same folder. It restores VRChat's vanilla `yt-dlp.exe`, removes the hosts entry, wipes `%LOCALAPPDATA%Low\WKVRCProxy\`, and deletes its own install directory.

---

## When something breaks

The watchdog window scrolls one summary line per resolve. Green = resolved, yellow = server replied with og-fallback, red = local timeout or pipe error. Per-URL detail goes to `%LOCALAPPDATA%Low\WKVRCProxy\logs\`.

Common failures and what to look at, in order: did the watchdog start? Does the watchdog show `[mesh] connected`? Does the watchdog show resolves when you paste a URL in-game? See the [Troubleshooting](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Troubleshooting) wiki page for the exact log lines to grep and what each one means.

---

## How it fits together

```
VRChat (AVPro)
   |  paste URL
   v
Tools/yt-dlp.exe          (our patched shim)
   |  named pipe
   v
WKVRCProxy.exe            (watchdog)
   |  WebSocket
   v
whyknot.dev /mesh         (remote resolver)
   |  resolves URL
   v
... resolved URL streamed back through the watchdog's local listener
... AVPro fetches via http://localhost.youtube.com:{port}/play?target=...
... and plays.
```

If any link breaks, the patched shim execs `yt-dlp-og.exe` and AVPro plays whatever vanilla yt-dlp returns. The watchdog absent removes the server-side path; videos that already worked with vanilla yt-dlp keep working.

The remote resolver is whyknot.dev. The wire protocol is documented in the [Mesh Protocol](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Mesh-Protocol) wiki page; if you want to host your own backend you'll find the details there.

---

## Going deeper

- [Quick Start](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Quick-Start) -- first-run walkthrough and the UAC prompts to expect
- [Mesh Protocol](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Mesh-Protocol) -- v3 wire details, msgpack negotiation, welcome cache, fallback paths
- [Troubleshooting](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Troubleshooting) -- log-line greps for every common failure mode
- [Update and Uninstall](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Update-and-Uninstall) -- what the bundled updater and uninstaller actually do
- [Engineering Standards](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Engineering-Standards) -- contributor rules

---

## License

Licensed under the GNU General Public License v3.0 or later. See [LICENSE](LICENSE) for the full text and [NOTICE](NOTICE) for third-party attributions covering binaries shipped in release archives.
