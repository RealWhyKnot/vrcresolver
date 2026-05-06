# Release Notes

Curated release history. The full per-commit log lives in [[Changelog]] (auto-maintained by the changelog-append workflow); this page summarizes user-visible changes per release.

> **Status: maintenance mode.** No formal v1.0.0 yet. The dist track ships continuous builds tagged `vYYYY.M.D.N` (CalVer); each tagged release attaches a SHA-256-verified zip via `release.yml`. This page mirrors the GitHub release notes for the recent tags; older entries roll off the bottom.

## How to read this page

Each entry covers what shipped between the prior tag and this one. Bullets follow conventional-commit shape (`feat:` / `fix:` / `perf:` / `refactor:`); doc-only and tooling-only commits are filtered out and live in the auto-changelog instead.

The bundled updater (`WKVRCProxy.Updater.exe`) is the canonical upgrade path. Run it; it pulls the newest tagged release, SHA-verifies the zip against the release body, and atomically swaps the files alongside the running watchdog. See [[Update and Uninstall]].

## Recent

_The auto-changelog workflow appends bullets here on tag push. The list below is the tail; full history lives in [[Changelog]]._

### Trust gateway phase 1 (current)

- **feat(relay): localhost.youtube.com HTTP trust gateway.** Watchdog binds an ephemeral high port on 127.0.0.1; the wrapper rewrites resolved URLs to `http://localhost.youtube.com:{port}/play?target=<base64>`. AVPro's trust list accepts the URL because the hosts file pins `localhost.youtube.com -> 127.0.0.1`. HLS manifest segment URLs get the same wrap so AVPro's per-segment fetches also pass the trust check. AOT-clean. HTTPS + cert lifecycle is parked behind concrete trigger conditions.
- **fix(mesh): tolerate v3.1 control frames on binary dispatch path.** Defense-in-depth against a future server regression that routes pong/ping/protocol_error/rate_limited through the binary path on a msgpack-negotiated connection. Closes the multi-hour reconnect storm pattern observed in the URL-failure audit.
- **chore(logging): tighten console output.** No hidden tiers; visible activity yes, in-place updates / spinners / churn no. File log carries the same content as console.

### v3.2 disk cache

- **feat(resolve-cache): per-(url, player, format, node) disk cache for hot resolves.** First resolve of a URL takes 2-3 seconds end-to-end. Second resolve in the same session lands in 20 ms (99.2% wrapper-side latency reduction). Cache file at `%LOCALAPPDATA%Low\WKVRCProxy\resolve_cache.json`. 30-second safety margin against server-supplied `expires_at`, 5-minute TTL fallback when the server omits expiry, 500-entry cap. Cache evicts on `silent_stall` and `load_failure` events from VrcLogMonitor.
- **build: AOT publish + msgpack source-gen.** Watchdog binary 41.16 MB self-contained -> 8.97 MB AOT (-78%). Cold start to mesh-connected ~486 ms. Plus the wrapper at 3.27 MB AOT.

### v3.1 msgpack hot path

- **feat(mesh): v3.1 msgpack hot-path decoder.** Server -> client hot path negotiates MessagePack on `client_hello.accept_formats=["msgpack","json"]`. Wire size 60-72% smaller than JSON; decode 67% faster. Welcome and control frames stay JSON-Text for debuggability. Wrapper still receives JSON over the local pipe (transcoded in the watchdog) so the wrapper stays small.

### v3.0 wire compression

- **feat(mesh): v3 client_hello + welcome_cached handshake.** `whyknot-v3` subprotocol on the WebSocket upgrade, RFC 7692 permessage-deflate, per-node welcome cache. 81% size reduction on the welcome frame on reconnect. v2 backwards-compat preserved.

### Earlier

For the full list see [[Changelog]] or [GitHub Releases](https://github.com/RealWhyKnot/WKVRCProxy/releases).

## What's parked

These features have plans but aren't shipping:

- **HTTPS + cert lifecycle for the trust gateway.** Phase 1 (HTTP-only) ships in production. Phase 2 plan is preserved with concrete trigger conditions; see [[Trust Gateway]] for the conditions and the original Plan-1-phase-2 design.
- **Custom plugin extractor for nepu.to.** Site is hard-blocked by Cloudflare for the WARP egress IP and challenges residential IPs with a JS interstitial. Browser-cookie capture would unblock it but adds operational overhead. Will revisit if a user supplies a HAR capture.

## How to roll back

The updater stages the new version under `.new-<short>` sidecars, renames into place atomically, and only stops the running watchdog AFTER the new payload is downloaded + SHA-verified + extracted. If a release ships with a regression, run an older zip from [Releases](https://github.com/RealWhyKnot/WKVRCProxy/releases) and extract over the install directory. The `Tools\yt-dlp.exe` patch swap is idempotent; the next tick re-applies whatever shim is alongside the watchdog binary.
