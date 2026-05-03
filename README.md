# WKVRCProxy

**Tiny tool that points VRChat at the patched yt-dlp.**

A Windows console daemon that swaps VRChat's bundled `yt-dlp.exe` for a patched build that asks our resolver server for help, and falls back to vanilla yt-dlp if anything is unreachable. Stays out of your way otherwise.

> **Status: alpha.** Works, but expect rough edges. Run it alongside VRChat; close it when you're done.

**[Latest release](https://github.com/RealWhyKnot/WKVRCProxy/releases/latest)** · **[Wiki](https://github.com/RealWhyKnot/WKVRCProxy/wiki)** · **[Report a bug](https://github.com/RealWhyKnot/WKVRCProxy/issues/new?template=bug_report.yml)**

---

## What it does

1. Backs up VRChat's `yt-dlp.exe` to `yt-dlp-og.exe` and replaces it with a patched build that talks to this watchdog over a local named pipe.
2. Holds a persistent WebSocket to whyknot.dev's `/mesh` endpoint.
3. When VRChat asks `yt-dlp.exe` to resolve a URL, the patched binary forwards the request through the pipe → WS → server, returns the resolved stream URL, and AVPro plays it.
4. If the server is unreachable, the resolver server bails on a particular URL, or this watchdog isn't running at all, the patched `yt-dlp.exe` execs `yt-dlp-og.exe` (the vanilla copy we preserved) and you get vanilla yt-dlp behaviour.

The watchdog absent doesn't break VRChat. It just removes the server-side path; videos that worked with vanilla yt-dlp keep working.

---

## Get it running

1. **Launch VRChat once first.** The patch needs VRChat's own `yt-dlp.exe` present in `Tools/` to preserve as the fallback.
2. **Download** the latest `WKVRCProxy-*.zip` from [Releases](https://github.com/RealWhyKnot/WKVRCProxy/releases/latest).
3. **Extract** anywhere except `Program Files` (the bundled updater can't swap files there without elevation).
4. **Run** `WKVRCProxy.exe`. UAC prompts once to add `127.0.0.1 localhost.youtube.com` to your hosts file (load-bearing for public-instance support; idle when this tool isn't running).
5. **Start VRChat.** Watch the watchdog window for the `[mesh] connected` line. Play a video in-world.

To uninstall: run `WKVRCProxy.Uninstaller.exe` from the same folder. It restores VRChat's vanilla `yt-dlp.exe`, removes the hosts entry, wipes `%LOCALAPPDATA%\WKVRCProxy\`, and deletes its own install directory. No prompt — running it IS consent.

---

## Going deeper

- **[Quick Start](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Quick-Start)** — first-run walkthrough
- **[Build Pipeline](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Build-Pipeline)** — how the release zip is produced
- **[Update and Uninstall](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Update-and-Uninstall)** — how the bundled updater/uninstaller behave
- **[Troubleshooting](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Troubleshooting)** — when something fails
- **[Development](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Development)** — build from source, contribute

The full wiki: <https://github.com/RealWhyKnot/WKVRCProxy/wiki>.

---

## License

[MIT](LICENSE).
