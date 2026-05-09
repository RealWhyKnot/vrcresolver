# Changelog

All notable changes to WKVRCProxy. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses date-driven versioning (`YYYY.M.D.N` for releases, `YYYY.M.D.N-XXXX` for dev builds -- see [[Build Pipeline]] for shape rules).

The most recent release is at the top.

<!-- Entries under "## Unreleased" are appended automatically by the changelog-append GitHub
     workflow on every push to main, then promoted to the versioned section by release.yml when
     a tag is cut. Don't hand-edit Unreleased -- your edits will be overwritten on the next push.
     To override an entry, amend the commit subject before merge. -->

## Unreleased

### Added
- **observability:** Tier4 background probe + stuck-loop detector (a229c82)
- **uninstaller:** No-prompt teardown (kill, restore, hosts, wipe, self-delete) (d007447)
- **updater:** GitHub Releases check + 15s prompt + replace + relaunch (f42dcc5)
- **client:** Wire Program.cs lifecycle (mutex + startup/shutdown) (aa7bbfc)
- **client:** Named-pipe server for the patched yt-dlp.exe (c0ed4d5)
- **client:** Mesh WS client (apex-302 + reconnect + heartbeat) (9d16c22)
- **client:** Lift PatchManager + HostsManager + path/hash utils (eeb8c81)
- **shared:** V2 protocol -- DTO + constant extensions (f1c2e8a)
- **mesh:** V2 protocol -- lossless forward, welcome handshake, fallback logging (a2179f1)
- **mesh:** Logging hardening (C3 + C4 + H7 + H8 + H9 + H10) (b8bd27c)
- **ipc:** Logging hardening + fallback frame stays v1-shape (H13+H14+H21+BC2) (1cf83da)
- **crash:** State-snapshot delegate enriches postmortem logs (H20) (a1a9b74)
- **yt-dlp:** Restore patched yt-dlp wrapper as 5th project (R1) (586ef85)
- Restore silent codec auto-install (R3) (58937d9)
- Runtime updater for bundled yt-dlp fallback (R4) (4844988)
- Anonymous failure reporting opt-in (R5) (41d38a1)
- VrcLogMonitor + playback_feedback mesh action (R2) (87d273b)
- **watchdog:** [via lh-yt] resolve tag + 30-min heartbeat with stats (b6490da)
- **watchdog:** Periodic hosts entry tick + tightened parser + tests (0e2a1e6)
- **logger:** Tee Console.Error too; expose Logger.Close() (4aeac2e)
- **mesh:** V3 client_hello + welcome_cached handshake (c3ecf12)
- **protocol:** V3 wire DTOs + WelcomeCache + MeshJsonContext (fbad93c)
- **mesh:** V3.1 msgpack hot-path decoder (7ccf4c6)
- **protocol:** V3.1 client_hello.accept_formats + welcome.negotiated_format (85878b7)
- **resolve-cache:** Per-(url,player,format,node) disk cache for hot resolves (3e6f0c6)
- **relay:** Plan 1 phase 1 -- localhost.youtube.com HTTP trust gateway (b27772f)
- **relay:** Dev-build verbose request trace gated on BuildInfo.IsDevBuild (0745ed4)

### Changed
- scaffold: 4-project solution (WKVRCProxy + Updater + Uninstaller + Shared) (4174228)
- Buffered pipe reads + pre-baked WS frames + WS keepalive off (546172c)
- Cache patched yt-dlp hash + size precheck (T1.2) (129d864)
- ux: patch first, then UAC prompt fire-and-forget (T1.6) (677cb94)
- ux: richer startup banner with key paths + OS/runtime (T2.6) (e243a70)
- diag: persistent rolling log file (T2.4) (bdfd60b)
- sec: redact UserName + USERPROFILE from crash logs (T5.7) (ba6b808)
- ux: startup 'update available' line (R6) (23166cf)
- diag(yt-dlp wrapper): full per-invocation log + correct empty-stdout-on-failure (6d5d6bb)
- ux(watchdog): user-friendly per-resolve console summary (e5d1256)
- ux(watchdog): show server fallback reason + suppress redundant mesh line (b78a5d9)
- **wrapper:** AOT-publish the yt-dlp wrapper (~79 MB -> ~3 MB) (4f35e3d)
- **wrapper:** Source-gen JSON via WrapperJsonContext (1249e43)
- **mesh:** Pass raw response bytes through; drop JsonDocument re-encode (314a043)
- **ipc:** Coalesce payload+newline into one WriteAsync; drop FlushAsync (f7956ac)
- **wrapper:** Drop unused Console.OutputEncoding=UTF8 setup (98cd5d6)
- **wrapper:** Reuse one FileStream for log writes; CreateDirectory once (6f59c49)
- Source-gen regexes for Updater + Shared (a2108be)
- Persist client_id across runs + clarify v3 stamping comments (475ba2a)
- harden(state): cap state-file reads + clean tmp residue + clarify deflate trust (13e308b)
- **regex:** GeneratedRegex source-gen across watchdog (92ece39)
- **json:** Route every JsonSerializer site through source-gen contexts (e6f5df5)
- Bump the minor-and-patch group with 2 updates (#55) (ed7b866)
- Bump Microsoft.NET.Test.Sdk from 17.14.1 to 18.5.1 (#56) (7a7d755)
- ux(watchdog): centralised console formatter -- per-component colour, fixed-column resolve line, boxed banner (b090c0d)
- Split proxy modules and keep local builds in dist (a925637)
- **relay:** Lighten localhost trust gateway (9f8c6d4)

### Fixed
- **patch:** Atomic file ops in PatchManager (no partial-file windows) (038359d)
- **uninstaller:** Atomic yt-dlp.exe restore (no missing-file window) (4b01682)
- **patch:** Write clean_exit.flag only when shutdown actually was clean (db56e4c)
- **patch:** Halt path restores yt-dlp.exe before exiting the loop (1a37bb9)
- **lifecycle:** Signal handlers register first + cover console-close events (5aef233)
- **updater:** SHA256 verify + atomic CopyOver + download-then-stop reorder (ddb1752)
- **observability:** Crash handlers in all three exes (07048f6)
- **cleanup:** Sweep watchdog-authored sidecars from VRChat Tools dir (f2a1773)
- **patch:** Recovery's orphan-patched path replaces with bundled vanilla (2161295)
- **lifecycle:** UnhandledException triggers cleanup directly (8b2bde2)
- **console:** Force UTF-8 output so em-dashes don't render as ? (3670dc3)
- **updater:** Atomic rollback registers backup before move attempt (92250fc)
- **mesh:** Welcome handshake hardening (H1 + H2 + H3) (4af6ac5)
- **mesh:** Apex 302 relative redirects resolve correctly (21727bb)
- **mesh:** Null-safety + TOCTOU + protocol_version stamp gating (H4+H5+BC1) (a0c9d05)
- **ipc:** Validate Player + Action at the pipe boundary (H11+H12) (977be5a)
- **patch:** Start + StopAsync are now idempotent (H15 + H16) (9aa3501)
- **updater:** Graceful watchdog stop + dev-tag parsing + missing-exe error (13c7ff9)
- **cleanup-invariant:** Wrapper log out of VRChat Tools dir (365d99c)
- **patch:** File-lock probe + VRChat-running banner (crash mitigation) (3b7800e)
- **integrity:** Relocate state to LocalLow + Low-integrity SACL on pipe (8173dc3)
- **integrity:** Pipe label via P/Invoke; skip logs in migration (fa33565)
- **integrity:** Create pipe with embedded SACL via CreateNamedPipe P/Invoke (cb2838f)
- **updater:** Mutex-poll release, retry rename, hint locked-file cause (0c91a02)
- **updater:** HTTP timeout + Content-Type + corp-proxy + temp sweep (41cb8d6)
- **uninstaller:** Wipe LocalLow state, scope kills, per-step breadcrumbs (b570aca)
- **mesh:** Tolerate v3.1 control frames on binary dispatch path (c9b7ece)
- **relay:** Unwrap trust-list segments + strip CF response headers (8f85b10)
- **relay:** Segment id-registry replaces base64 wrap; all bytes via whyknot.dev (06927bc)
- **relay:** Decompress upstream HLS so we don't lie about Content-Encoding (947aae5)
- **release:** Map git-author WhyKnot -> @RealWhyKnot + pragma CS0162 dev banner (7f6e246)
- **build:** Treat CI-tagged release builds as release-mode regardless of dirty state (370878f)
- **release:** Scope release body to the just-this-version commit slice only (fa44a75)
- **relay:** /play/<hex><ext> path form for AVPro/MF byte-stream dispatch (7673d6c)
- **relay:** Recognise /api/proxy/<ext> token form when extracting extension (0dac0a6)
- **release:** Retry release-body verify to tolerate GitHub API settle time (a4291aa)
- **relay:** Redirect to .m3u8 path when HLS body served at non-HLS extension (158e266)

---

## [v2026.4.28.0](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.4.28.0) -- 2026-04-28

_Maintenance release; see commit log for details._

---

## [v2026.4.27.3](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.4.27.3) -- 2026-04-27

### Added
- **ui:** Promote website embed to permanent surface + native bridge (b53c591)
- **ui:** Website-tab embed PoC, dark behind enableWebsiteTab flag (a795603)
- **ui:** In-app changelog viewer + uptime persistence + traffic format hardening (7992bea)

### Fixed
- **ui:** Align wkBridge with canonical handshake + response shape (b6e9c14)
- **relay:** Drop dead PO token visitor_data branch (1da7139)
- **updater:** Require SHA256 in release notes; reject Zip Slip on extract (7c465d1)

---

## [v2026.4.27.2](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.4.27.2) -- 2026-04-27

### Fixed
- **Updater silently failing when run from inside the install dir.** `Directory.Move` can't rename a folder containing the running process's image -- the swap was failing with `ERROR_SHARING_VIOLATION` and the console flashed shut without a `Pause()`. Symptom: `version.txt` never updated and the "Update available" banner kept reappearing on next launch. The updater now self-copies to `%TEMP%` and re-execs from there before doing the swap.
- **`updater.exe` triggering Windows' installer-detection auto-UAC prompt.** The filename matches Windows' "looks like an installer" heuristic, so the OS auto-prompted for elevation. Users who declined got `ERROR_CANCELLED`. Both `updater.exe` and `uninstall.exe` now ship an `app.manifest` with `requestedExecutionLevel="asInvoker"` to opt out of the heuristic.
- **Pause-on-error coverage.** Every error return path in the updater now calls `Pause()`, so failures stop being silent.

### Added
- **Catastrophic-failure recovery screen.** When both the install-dir swap *and* the rollback fail (the worst case -- install gone, no rollback), the updater now prints a prominent banner with the backup-folder path, opens the GitHub releases page in the user's browser, and tells them to re-download manually.
- **Uninstaller now removes the `127.0.0.1 localhost.youtube.com` hosts entry.** The uninstaller spawns a copy of itself with `Verb=runas` to get the one UAC prompt needed to edit `system32\drivers\etc\hosts`. Declining UAC leaves the line in place with a log warning.
- **"Retry with admin elevation" fallback in the UI** when the normal sidecar launch returns `ERROR_CANCELLED` or similar. The retry runs PowerShell `Unblock-File` against the sidecar exe (strips SmartScreen's Mark-of-the-Web) and re-launches with explicit `Verb=runas`.

### Changed
- **Large files split into partial-class siblings** for readability. No behavior change -- partial classes compile to identical IL.
  - `ResolutionEngine.cs`: 2298 -> 1004 lines, with five sibling files: `.YtDlpProcess.cs` (subprocess invocation), `.Tiers.cs` (tier execution), `.ColdRace.cs` (race orchestration), `.UrlClassification.cs` (host/URL helpers + relay wrap), `.PlaybackFeedback.cs` (demote loop + recent-resolutions ring).
  - `WKVRCProxy.UI/Program.cs`: 869 -> 516 lines, with `Program.IpcHandlers.cs` carrying the webview message dispatch + sidecar launcher.
  - `appStore.ts`: 737 -> 602 lines, with `appStore.types.ts` extracted (re-exported so consumer imports keep working).

---

## [v2026.4.27.1](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.4.27.1) -- 2026-04-27

### Added
- **Anonymous failure reporting (opt-in).** When every resolution tier fails for a URL, WKVRCProxy can POST a sanitized failure summary to `whyknot.dev/api/report`, which forwards to a private Discord channel for triage. Off by default; the first cascade failure after install surfaces a modal showing the *exact* JSON that would be sent so the user can review before opting in.
- **Privacy contract** enforced client-side and re-validated server-side: full URL is never transmitted (only the bare domain), path/query and YouTube video IDs are reduced to a 12-char SHA-256 prefix for de-duplication, free-form error strings run through a sanitizer that strips Windows usernames, Linux home dirs, drive-letter paths, IPv4 literals, the machine hostname, and long token-shaped sequences. 17 unit tests pin the behavior.
- **Client-side rate limit** of 1 report per 30 s and 20 per session. Server adds a per-IP 10/hour sliding window on top.
- **Settings -> Network -> Anonymous Reporting toggle** so users can change their answer later.
- **Wiki entries for Mask IP and per-field override tracking** in [[Settings-Reference]] and [[Resolution-Cascade]].

---

## [v2026.4.27.0](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.4.27.0) -- 2026-04-27

### Added
- **Mask IP mode.** A single Settings toggle that forces every origin-facing strategy (tier-1 yt-dlp, tier-3 yt-dlp-og, headless browser-extract) through the local Cloudflare WARP SOCKS5 proxy, masking the user's real IP from origin servers. The whyknot.dev cloud resolver intentionally stays direct -- it's a trusted endpoint and routing the cloud call through WARP would just add latency. WARP-unavailable means strategies abort with a loud warning rather than silently falling back to direct egress (no real-IP leak). WARP eager-starts on launch and on toggle off->on so the first request after enabling doesn't pay wireproxy's cold-start latency.
- **`tier1:warp+default` and `tier1:warp+vrchat-ua` in the default strategy priority list.** The `wgcf` + `wireproxy` binaries already ship with every build; the variants now appear at the tail of the priority list (priority 90/95) where they fire as the "try a different network" fallback. Suppressed automatically when Mask IP is on (they'd be byte-identical duplicates).

### Changed
- **Per-field config override tracking replaces the snapshot-migration scheme.** New `userOverriddenKeys` set in `app_config.json` records which default-tracked fields the user has explicitly customized. Fields *not* in the set re-pull the current source default on every load -- so editing a default constant in the codebase flows out to all users who haven't customized that field, with no version bump and no per-field migration code. Customized values are preserved verbatim across updates. Legacy configs (no `userOverriddenKeys` field on disk) auto-classify on first load by comparing values to frozen V1/V2 historical defaults.
- **Build versioning is now dual-shape.** `vYYYY.M.D.N` for releases (no suffix; `.N` is the release iteration for the day, starting at 0). `YYYY.M.D.N-XXXX` for local dev builds (`XXXX` = 4-hex UID disambiguating rebuilds at the same `.N`). Both `build.ps1` and the commit-msg hook validate against the same regex; `AppUpdateChecker` and `updater.exe` both strip the suffix before comparing.

### First public release.

---

## Tag history

Earlier internal builds shipped under tags `v2026.02.23.x` and `v2026.4.21.x` to `v2026.4.27.0-0001` (since retagged to `v2026.4.27.0`). Those predate this changelog; the [release page](https://github.com/RealWhyKnot/WKVRCProxy/releases) is the authoritative archive.
