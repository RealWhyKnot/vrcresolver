# Engineering Standards

Rules that contributors MUST follow. These exist because of real bugs we've hit; if you're tempted to break a rule, read why first.

## Hard rules

### Never break the og-fallback path

**The watchdog must always leave VRChat with a working `yt-dlp.exe` it can reach.** If the watchdog dies mid-flight, if the user kills it, if a swap goes wrong — the patched yt-dlp must still be able to exec `yt-dlp-og.exe` and fall back to vanilla yt-dlp behaviour.

**Why**: The watchdog absent ≠ VRChat broken. That invariant is the difference between a hobby tool and something users will keep installed. The bundled `tools/yt-dlp-og-fallback.exe` exists specifically so that if the user's `yt-dlp-og.exe` goes missing for any reason, the watchdog can drop a known-good vanilla copy back into VRChat's Tools dir on the next 3 s tick.

**How to apply**: Don't add code paths that delete `yt-dlp-og.exe` without immediately copying the bundled fallback in. Don't tighten `PatchManager.Start()`'s refusal conditions in a way that leaves an orphan patched binary in VRChat's Tools dir without a fallback alongside it. Test the "kill -9 the watchdog mid-tick" case manually whenever you touch `PatchManager`.

### Wrap is contextual; don't widen scope

**The `127.0.0.1 localhost.youtube.com` hosts entry exists to bypass VRChat's trusted-URL allowlist in public instances.** It's not a general-purpose intercept. The watchdog adds it once on first run and removes it on uninstall.

**Why**: Public worlds with "Allow Untrusted URLs" off enforce AVPro's small allowlist (`*.youtube.com` and friends). Without the hosts entry, server-resolved URLs fail at playback. In private/friends instances the toggle is on and the entry is idle — but documented behaviour is "load-bearing for public-instance support, idle otherwise", and that's how user-facing copy should describe it.

**How to apply**: Don't pin additional hostnames. Don't repurpose this entry for resolution-side work. If a future change needs more allowlist coverage, do it server-side, not by polluting the user's hosts file.

### Don't blame the user first

When something doesn't work, exhaust the watchdog's own logs before deflecting to "check your network / antivirus / VRChat install". Most failures show up clearly in the console; the user-facing copy should reflect that and point them there.

### Don't revert foreign edits

If you see uncommitted changes you didn't make (a manifest you don't recognise, a folder you don't recognise, an orphan file in `src/`), assume parallel work is in flight and **leave it**. Add only your own files; commit only your own staged paths.

**Why**: This repo regularly has multiple workstreams running concurrently. Reverting unfamiliar edits has caused multi-hour rework before.

**How to apply**: `git add <specific paths>` rather than `git add -A`. Inspect `git status` before every commit. If unsure, ask before committing.

## Commit conventions

Subject form: `type(scope?): short summary`

`type` ∈ `feat`, `fix`, `build`, `docs`, `refactor`, `test`, `chore`. `scope` is optional but useful for cross-cutting changes (e.g., `feat(client): …`).

`build.ps1` appends a `(YYYY.M.D.N-XXXX)` build-version stamp to commit subjects automatically. **Don't paste the same stamp twice in one subject** — `.githooks/commit-msg` rejects duplicates.

Body explains the *why*. The diff is the *what*; don't restate it.

## Code style

Follow the surrounding file. The project deliberately avoids heavy abstraction — a new feature usually means extending an existing class, not introducing a new one.

Favour:
- Plain methods over interfaces with one impl
- `record`s for pure data
- `async`/`await` for I/O; no fire-and-forget without an explicit reason
- Comments that explain *why* something non-obvious is the way it is

Avoid:
- Static state in business logic
- Cleanup commits bundled with feature commits

## Logging

The watchdog logs to its console only. No log file, no UI, no tier filtering. Anything worth telling the user goes to stdout.

> **No hidden log tiers.** If a line is noise, delete it; don't hide it. If a transition is meaningful (connect/disconnect/patch-applied/og-restored), print it once — don't spam.

## Security

- See [`SECURITY.md`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/.github/SECURITY.md) for the disclosure policy.
- Anything that touches the hosts file, patches a VRChat-owned file, or runs admin-elevated is in scope.
