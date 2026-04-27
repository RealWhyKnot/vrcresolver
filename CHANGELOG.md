# Changelog

All notable changes to WKVRCProxy. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses date-driven versioning (`YYYY.M.D.N` for releases, `YYYY.M.D.N-XXXX` for dev builds — see the [release reference](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Build-Pipeline) for shape rules).

The most recent release is at the top.

---

## [v2026.4.27.2](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.4.27.2) — 2026-04-27

### Fixed
- **Updater silently failing when run from inside the install dir.** `Directory.Move` can't rename a folder containing the running process's image — the swap was failing with `ERROR_SHARING_VIOLATION` and the console flashed shut without a `Pause()`. Symptom: `version.txt` never updated and the "Update available" banner kept reappearing on next launch. The updater now self-copies to `%TEMP%` and re-execs from there before doing the swap.
- **`updater.exe` triggering Windows' installer-detection auto-UAC prompt.** The filename matches Windows' "looks like an installer" heuristic, so the OS auto-prompted for elevation. Users who declined got `ERROR_CANCELLED`. Both `updater.exe` and `uninstall.exe` now ship an `app.manifest` with `requestedExecutionLevel="asInvoker"` to opt out of the heuristic.
- **Pause-on-error coverage.** Every error return path in the updater now calls `Pause()`, so failures stop being silent.

### Added
- **Catastrophic-failure recovery screen.** When both the install-dir swap *and* the rollback fail (the worst case — install gone, no rollback), the updater now prints a prominent banner with the backup-folder path, opens the GitHub releases page in the user's browser, and tells them to re-download manually.
- **Uninstaller now removes the `127.0.0.1 localhost.youtube.com` hosts entry.** The uninstaller spawns a copy of itself with `Verb=runas` to get the one UAC prompt needed to edit `system32\drivers\etc\hosts`. Declining UAC leaves the line in place with a log warning.
- **"Retry with admin elevation" fallback in the UI** when the normal sidecar launch returns `ERROR_CANCELLED` or similar. The retry runs PowerShell `Unblock-File` against the sidecar exe (strips SmartScreen's Mark-of-the-Web) and re-launches with explicit `Verb=runas`.

### Changed
- **Large files split into partial-class siblings** for readability. No behavior change — partial classes compile to identical IL.
  - `ResolutionEngine.cs`: 2298 → 1004 lines, with five sibling files: `.YtDlpProcess.cs` (subprocess invocation), `.Tiers.cs` (tier execution), `.ColdRace.cs` (race orchestration), `.UrlClassification.cs` (host/URL helpers + relay wrap), `.PlaybackFeedback.cs` (demote loop + recent-resolutions ring).
  - `WKVRCProxy.UI/Program.cs`: 869 → 516 lines, with `Program.IpcHandlers.cs` carrying the webview message dispatch + sidecar launcher.
  - `appStore.ts`: 737 → 602 lines, with `appStore.types.ts` extracted (re-exported so consumer imports keep working).

---

## [v2026.4.27.1](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.4.27.1) — 2026-04-27

### Added
- **Anonymous failure reporting (opt-in).** When every resolution tier fails for a URL, WKVRCProxy can POST a sanitized failure summary to `whyknot.dev/api/report`, which forwards to a private Discord channel for triage. Off by default; the first cascade failure after install surfaces a modal showing the *exact* JSON that would be sent so the user can review before opting in.
- **Privacy contract** enforced client-side and re-validated server-side: full URL is never transmitted (only the bare domain), path/query and YouTube video IDs are reduced to a 12-char SHA-256 prefix for de-duplication, free-form error strings run through a sanitizer that strips Windows usernames, Linux home dirs, drive-letter paths, IPv4 literals, the machine hostname, and long token-shaped sequences. 17 unit tests pin the behavior.
- **Client-side rate limit** of 1 report per 30 s and 20 per session. Server adds a per-IP 10/hour sliding window on top.
- **Settings → Network → Anonymous Reporting toggle** so users can change their answer later.
- **Wiki entries for Mask IP and per-field override tracking** in [Settings-Reference](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Settings-Reference) and [Resolution-Cascade](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Resolution-Cascade).

---

## [v2026.4.27.0](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.4.27.0) — 2026-04-27

### Added
- **Mask IP mode.** A single Settings toggle that forces every origin-facing strategy (tier-1 yt-dlp, tier-3 yt-dlp-og, headless browser-extract) through the local Cloudflare WARP SOCKS5 proxy, masking the user's real IP from origin servers. The whyknot.dev cloud resolver intentionally stays direct — it's a trusted endpoint and routing the cloud call through WARP would just add latency. WARP-unavailable means strategies abort with a loud warning rather than silently falling back to direct egress (no real-IP leak). WARP eager-starts on launch and on toggle off→on so the first request after enabling doesn't pay wireproxy's cold-start latency.
- **`tier1:warp+default` and `tier1:warp+vrchat-ua` in the default strategy priority list.** The `wgcf` + `wireproxy` binaries already ship with every build; the variants now appear at the tail of the priority list (priority 90/95) where they fire as the "try a different network" fallback. Suppressed automatically when Mask IP is on (they'd be byte-identical duplicates).

### Changed
- **Per-field config override tracking replaces the snapshot-migration scheme.** New `userOverriddenKeys` set in `app_config.json` records which default-tracked fields the user has explicitly customized. Fields *not* in the set re-pull the current source default on every load — so editing a default constant in the codebase flows out to all users who haven't customized that field, with no version bump and no per-field migration code. Customized values are preserved verbatim across updates. Legacy configs (no `userOverriddenKeys` field on disk) auto-classify on first load by comparing values to frozen V1/V2 historical defaults.
- **Build versioning is now dual-shape.** `vYYYY.M.D.N` for releases (no suffix; `.N` is the release iteration for the day, starting at 0). `YYYY.M.D.N-XXXX` for local dev builds (`XXXX` = 4-hex UID disambiguating rebuilds at the same `.N`). Both `build.ps1` and the commit-msg hook validate against the same regex; `AppUpdateChecker` and `updater.exe` both strip the suffix before comparing.

### First public release.

---

## Tag history

Earlier internal builds shipped under tags `v2026.02.23.x` and `v2026.4.21.x` to `v2026.4.27.0-0001` (since retagged to `v2026.4.27.0`). Those predate this changelog; the [release page](https://github.com/RealWhyKnot/WKVRCProxy/releases) is the authoritative archive.
