# WKVRCProxy

**Make VRChat's in-world video players actually work.**

A Windows desktop app that runs alongside VRChat and rebuilds the video-resolution pipeline so URLs that fail with `[AVProVideo] Error: Loading failed.` actually play.

> **Status: alpha.** It works, but expect rough edges. Run it alongside VRChat; close it if something misbehaves.

**[Latest release](https://github.com/RealWhyKnot/WKVRCProxy/releases/latest)** · **[Wiki](https://github.com/RealWhyKnot/WKVRCProxy/wiki)** · **[Report a bug](https://github.com/RealWhyKnot/WKVRCProxy/issues/new?template=bug_report.yml)**

---

## The problem

VRChat's in-world video players use a bundled `yt-dlp.exe` to resolve URLs and AVPro Video to play them. The resolver is where most failures live: YouTube progressively raises the bar — PO tokens, `visitor_data` binding, browser TLS fingerprints — and the vanilla yt-dlp shipped with VRChat can't keep up. The result is a spinner that never resolves, then `[AVProVideo] Error: Loading failed.` This bites every user, in every instance type, public or private.

A separate failure mode appears when AVPro's trusted-URL list is in force — typically public-world play with **Allow Untrusted URLs** off in your VRChat comfort settings. Hosts off the small allowlist (`*.youtube.com`, `*.vrcdn.live`, and a handful of others) fail with the same `Loading failed` before they ever get to play. In private and friends-only instances where users have untrusted URLs allowed, the trust list isn't enforced and that second failure mode goes away on its own. WKVRCProxy works around both.

---

## What it does for you

- **More videos resolve.** WKVRCProxy runs several resolution methods in parallel for every URL — vanilla yt-dlp, a PO-token-equipped variant, browser-TLS-impersonating clients, a headless-browser fallback, mobile clients, a Cloudflare-WARP egress path, and a cloud resolver. The first one that succeeds wins. Videos that fail with the bundled yt-dlp typically work through one of these.
- **Smarter every time.** WKVRCProxy remembers which method worked for which host. The next request from the same source skips the race and goes straight to the known-good method.
- **Trust-list bypass when you hit it.** A local relay makes a non-allowlisted URL look like it's coming from a trusted YouTube host, so AVPro plays it. Mostly relevant in public worlds with untrusted URLs disabled — a no-op everywhere else.
- **Heals from failures.** A log monitor watches VRChat for `Loading failed`. When that fires, WKVRCProxy demotes the strategy that returned the bad URL and the next request re-cascades. Failures push the system toward correctness instead of leaving it stuck on a strategy whose URLs don't actually play.
- **Drop in, drop out.** Run WKVRCProxy before VRChat, close it when you're done. The patch on VRChat's `yt-dlp.exe` is reverted on shutdown. No registry, no service, nothing system-wide except a single hosts-file line that's idle when WKVRCProxy isn't running.

---

## Get it running

1. **Download** the latest `WKVRCProxy-*.zip` from [Releases](https://github.com/RealWhyKnot/WKVRCProxy/releases/latest).
2. **Extract** it anywhere except `Program Files` (the in-app updater can't swap files there without elevation).
3. **Run** `WKVRCProxy.exe`. First launch asks for UAC once to add `127.0.0.1 localhost.youtube.com` to your hosts file.
4. **Start VRChat.** Play a video in-world. If something doesn't play, check the **Logs** tab.

Full walkthrough with troubleshooting: **[Quick Start](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Quick-Start)**.

---

## Going deeper

- **[Architecture](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Architecture)** — request flow end-to-end
- **[Resolution Cascade](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Resolution-Cascade)** — strategies, the parallel race, the demotion loop
- **[Relay Server](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Relay-Server)** — why URLs get wrapped
- **[Settings Reference](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Settings-Reference)** — every config knob
- **[Troubleshooting](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Troubleshooting)** — when something fails
- **[Development](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Development)** — build from source, run tests, contribute

The full wiki is at <https://github.com/RealWhyKnot/WKVRCProxy/wiki>.

---

## License

[MIT](LICENSE).
