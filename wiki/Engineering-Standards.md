# Engineering Standards

Rules that contributors MUST follow. These exist because of real bugs we've hit; if you're tempted to break a rule, read why first.

## Hard rules

### Never break the og-fallback path

**The watchdog must always leave VRChat with a working `yt-dlp.exe` it can reach.** If the watchdog dies mid-flight, if the user kills it, if a swap goes wrong -- the patched yt-dlp must still be able to exec `yt-dlp-og.exe` and fall back to vanilla yt-dlp behaviour.

**Why**: The watchdog absent != VRChat broken. That invariant is the difference between a hobby tool and something users will keep installed. The bundled `tools/yt-dlp-og-fallback.exe` exists specifically so that if the user's `yt-dlp-og.exe` goes missing for any reason, the watchdog can drop a known-good vanilla copy back into VRChat's Tools dir on the next 3 s tick.

**How to apply**: Don't add code paths that delete `yt-dlp-og.exe` without immediately copying the bundled fallback in. Don't tighten `PatchManager.Start()`'s refusal conditions in a way that leaves an orphan patched binary in VRChat's Tools dir without a fallback alongside it. Test the "kill -9 the watchdog mid-tick" case manually whenever you touch `PatchManager`.

### Wrap is contextual; don't widen scope

**The `127.0.0.1 localhost.youtube.com` hosts entry exists to bypass VRChat's trusted-URL allowlist in public instances.** It's not a general-purpose intercept. The watchdog adds it on first run, re-checks it every minute via `HostsTicker`, and removes it on uninstall.

**Why**: Public worlds with "Allow Untrusted URLs" off enforce AVPro's small allowlist (`*.youtube.com` and friends). A user-pasted URL whose host matches `localhost.youtube.com` passes that allowlist check; the resolved stream URL (typically `*.googlevideo.com`) is also on the allowlist so playback works end-to-end. In private/friends instances the toggle is on and the entry is idle.

**How to apply**: Don't pin additional hostnames. Don't repurpose this entry for resolution-side work. If a future change needs more allowlist coverage, do it server-side, not by polluting the user's hosts file. Match the entry conservatively (token-aware, NOT substring) -- a comment line that mentions the marker is not a bypass entry, and neither is `127.0.0.1 notlocalhost.youtube.com`.

### AOT compatibility for the wrapper

**`src/WKVRCProxy.YtDlp/` ships as a native AOT binary.** Anything added there must be trim-safe and AOT-safe. JSON serialization MUST go through `WrapperJsonContext` (source-gen). No `Activator.CreateInstance`, no `Type.GetType(string)`, no `MakeGenericType`, no `Expression.Compile`, no reflection-based `JsonSerializer` calls. The trimmer will silently drop metadata at publish, then the AOT binary will throw `MissingMethodException` at runtime where dev builds worked.

**How to apply**: When extending the wrapper, run `dotnet publish src/WKVRCProxy.YtDlp -c Release -r win-x64` locally and confirm zero IL3050/IL2026 warnings. The watchdog/updater/uninstaller are NOT AOT'd and can use full reflection -- keep AOT-discipline scoped to `WKVRCProxy.YtDlp` only.

### Wire-protocol DTOs -- byte-exact with whyknot.dev + source-gen on the v3 path

**Any new field/action/feature added to `src/WKVRCProxy.Shared/Protocol.cs` MUST mirror whyknot.dev's `MeshResolveProtocol.cs` byte-exact** (literal string, casing, snake_case vs camelCase, type). A casing flip silently desyncs the wire and surfaces as "frame parse failed" hours later. Add a constant + a unit test in `WireConstants_*_match_server_spec_strings` so a desync is caught at build time, not at user playback.

**v3-path DTOs go through the source-gen `MeshJsonContext`** at `src/WKVRCProxy/MeshJsonContext.cs`, NOT reflection. Touching the v3 surface? Add the new type to `[JsonSerializable(typeof(...))]` so the path stays AOT-clean for the future watchdog AOT audit. The existing v2 `JsonSerializer.Deserialize<WelcomeFrame>` reflection call is intentionally left in place -- that's the watchdog AOT audit's scope. Don't bundle a v2-side conversion with v3 work.

### Integrity-level model -- Low wrapper, Medium watchdog, named-pipe SACL

**The patched yt-dlp wrapper runs at Low integrity** (inherited from VRChat's `Tools\` dir, which lives under `%LOCALAPPDATA%Low\`). The watchdog runs at Medium. The named pipe `\\.\pipe\WKVRCProxy.resolve` is created with an explicit SACL granting Low-integrity connect access.

**How to apply**: All persistent state goes under `%LOCALAPPDATA%Low\WKVRCProxy\` so the wrapper can write it. Use `WkvrcPaths.StateRoot()` / `WkvrcPaths.LogsDir()` -- never hard-code `Environment.SpecialFolder.LocalApplicationData`. Adding a new file the wrapper writes? Confirm the parent dir resolves under LocalLow.

### Trust gateway is HTTP-only; cert lifecycle is parked

**The trust-gateway HTTP listener at `localhost.youtube.com:{port}` runs plain HTTP on a non-443 port.** AVPro accepts plain `http://` for hostnames matching its allowlist; the legacy implementation proved this in production and the current Phase 1 implementation is verified live.

HTTPS upgrades + per-machine self-signed cert generation + `LocalMachine\Root` install + `netsh http add sslcert` + cert renewal in the updater + cert removal in the uninstaller add roughly 8 dev-days of work for a hardening that doesn't change correctness for any user we've observed. The Phase 2 plan stays drafted; resumption triggers are concrete (AVPro starts requiring HTTPS for trust-list hostnames, certificate-pinning behaviour changes in VRChat, security audit demands TLS-everywhere, user reports public-instance failure with TLS-shaped log signature). See [[Trust Gateway]] for the trigger conditions and the Phase 2 design.

**How to apply**: Don't half-ship HTTPS. If a trigger fires, ship the full lifecycle in one chunk -- bootstrap + listener + updater + uninstaller. HTTPS-in-listener-but-no-cert-in-installer leaves users in a worse state than HTTP-only.

## Commit conventions

Subject form: `type(scope?): short summary`

`type` in `feat`, `fix`, `build`, `docs`, `refactor`, `test`, `chore`. `scope` is optional but useful for cross-cutting changes (e.g., `feat(client): ...`).

`build.ps1` appends a `(YYYY.M.D.N-XXXX)` build-version stamp to commit subjects automatically. **Don't paste the same stamp twice in one subject** -- `.githooks/commit-msg` rejects duplicates.

**Don't add Claude / AI-attribution trailers to commit messages.** No `Co-Authored-By: Claude`, no `Generated by Claude Code`, no agent identifiers. Commits are authored by the human contributor.

Body explains the *why*. The diff is the *what*; don't restate it.

### Don't blame the user first

When something doesn't work, exhaust the watchdog's own logs before deflecting to "check your network / antivirus / VRChat install". Most failures show up clearly in the console; the user-facing copy should reflect that and point them there.

### Don't revert foreign edits

If you see uncommitted changes you didn't make (a manifest you don't recognise, a folder you don't recognise, an orphan file in `src/`), assume parallel work is in flight and **leave it**. Add only your own files; commit only your own staged paths.

**Why**: This repo regularly has multiple workstreams running concurrently. Reverting unfamiliar edits has caused multi-hour rework before.

**How to apply**: `git add <specific paths>` rather than `git add -A`. Inspect `git status` before every commit. If unsure, ask before committing.

## Code style

Follow the surrounding file. The project deliberately avoids heavy abstraction -- a new feature usually means extending an existing class, not introducing a new one.

Favour:
- Plain methods over interfaces with one impl
- `record`s for pure data
- `async`/`await` for I/O; no fire-and-forget without an explicit reason
- Comments that explain *why* something non-obvious is the way it is

Avoid:
- Static state in business logic
- Cleanup commits bundled with feature commits

## Logging

The watchdog tees `Console.Out` AND `Console.Error` to a rolling file at `%LOCALAPPDATA%Low\WKVRCProxy\logs\watchdog-<utc>.log` (10 MiB rotation, 7-day retention). The patched yt-dlp wrapper appends per-invocation traces to `yt-dlp-wrapper.log` in the same dir. Crash dumps land in `crashes/`. See [[Logs and Diagnostics]].

> **No hidden log tiers.** If a line is noise, delete it; don't hide it. If a transition is meaningful (connect/disconnect/patch-applied/og-restored), print it once -- don't spam. The console window and the file see the same content; `Logger.WriteFileOnly` is the one exception (used for verbose per-strategy server frames that would clutter the live console but are useful for grep / bug-report attachment).

## Security

- See [`SECURITY.md`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/.github/SECURITY.md) for the disclosure policy.
- Anything that touches the hosts file, patches a VRChat-owned file, or runs admin-elevated is in scope.
