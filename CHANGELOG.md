# Changelog

All notable changes to WKVRCProxy. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses date-driven versioning (`YYYY.M.D.N` for releases, `YYYY.M.D.N-XXXX` for development builds; see the [release reference](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Build-Pipeline) for shape rules).

Release entries are listed newest first. This changelog starts with the first public GitHub release.

<!-- Entries under "## Unreleased" are appended automatically by the changelog-append GitHub
     workflow on every push to main, then promoted to the versioned section by release.yml when
     a tag is cut. Keep this section public-facing and concise. To override an entry, amend the
     commit subject before merge or mark the commit [skip changelog]. -->

## Unreleased

### Added
- **helper:** Expand helper lease + resolve diagnostics (29d82cf)
- **helper:** Trust key challenge, encoder smoke test, pre-upload validation (9f913f4)
- **wrapper:** Retry resolve on discovery_in_progress with deadline-aware hold (49fb3b7)
- **wrapper:** Bump resolve deadline from 18s to 28s (c89e234)
- **wrapper:** Classify og-fallback content_not_found patterns (c7a4376)

### Fixed
- **wrapper:** Re-establish pipe per retry so resolve retries can actually send (5070472)
- **ipc:** Align watchdog per-request budget with wrapper deadline (c64298e)

---

## [v2026.5.14.0](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.5.14.0) - 2026-05-14

### Added
- **watchdog:** Add interactive terminal and HTTPS relay bootstrap (b0475d0)
- **watchdog:** Add advanced terminal renderer (11749d3)
- **helper:** Ship ffmpeg and handle transcode leases (f150370)
- **helper:** Add hitch diagnostics and benchmarked presets (036692e)
- **mesh:** Playback_feedback emits delivered_height + kind=playing telemetry (f133ffb)
- **helper:** Add ffmpeg hardware decode fallback (2caac69)

### Changed
- Improve helper diagnostics and terminal input (3988dad)
- **patcher:** Identify wrappers by marker; drop bundled yt-dlp (0286d54)

### Fixed
- **relay:** Stream-localize manifests so playback_id tokens dont 502 (fa74463)

---

## [v2026.5.10.4](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.5.10.4) - 2026-05-10

### Added
- Local WhyKnot trust gateway for VRChat video players, including `localhost.youtube.com` playback URLs that keep first-party WhyKnot streams inside the allowed playback path.
- Direct handling for pasted `whyknot.dev` playback proxy URLs, so a first-party proxy URL in a video player resolves to a local manifest instead of recursively re-resolving itself.
- Local HLS/DASH manifest localization for first-party WhyKnot proxy URLs, including child manifests and segment URLs with stable local names.
- Mesh client support for WhyKnot backend protocol negotiation, binary response handling, cached welcomes, reconnects, and playback feedback.
- WKVRCProxy updater and uninstaller executables with zip verification, rollback-aware updates, hosts cleanup, state cleanup, and install-folder removal.
- Persistent logs, crash snapshots, startup/runtime context, and redacted error reporting.

### Changed
- Reorganized the client into focused modules for URL policy, relay target validation, manifest rewriting, header forwarding, port tracking, resolve cache, and wrapper behavior.
- Hardened relay shutdown and cleanup so port files, patched binaries, sidecar files, state files, named pipes, and child processes are cleaned when possible.
- Build and release pipeline now signs tagged builds, emits a per-file SHA256 manifest, syncs wiki docs, and gates release notes for public wording and ASCII output.
- Documentation now describes the current watchdog-only architecture, trust gateway behavior, quick start, updater, uninstaller, build pipeline, and release process.

### Fixed
- Prevented non-WhyKnot URLs and local gateway URLs from being wrapped into the trust gateway accidentally.
- Rejected unsafe relay requests with invalid Host headers, unsupported HTTP methods, non-HTTP targets, or non-WhyKnot playback targets.
- Avoided stale localhost gateway cache entries by canonicalizing playback feedback back to first-party WhyKnot target URLs.
- Kept upstream manifest responses uncompressed while they are inspected so headers and body content stay consistent.
- Preserved third-party manifest URLs and data URIs during localization so external media references are not rewritten incorrectly.
- Added regression coverage for direct WhyKnot URLs, Popcorn proxy URLs, manifest localization, local-gateway canonicalization, cache eviction, target validation, and relay host validation.
