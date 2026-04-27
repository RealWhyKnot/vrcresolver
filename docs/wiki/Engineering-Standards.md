# Engineering Standards

Rules that future contributors MUST follow. These are not stylistic preferences — most exist because of a real bug we hit before. The reasons matter; if you're tempted to break a rule, read why first.

## Hard rules

### No C# string interpolation in stamped files

**Don't use `$"..."` anywhere `build.ps1` rewrites with regex.** Specifically: the assembly-version attributes in `.csproj` files and any constants in `Core/CoreJsonContext.cs` (and similar) that the build script rewrites to embed the version stamp.

**Why**: `build.ps1` writes the version with a regex pass over the file. Interpolated strings can contain quote-like characters and brace pairs that the regex misreads, leading to corrupted version stamps and persistent build breakage that's hard to attribute.

**How to apply**: Use `+` concatenation or `StringBuilder`. `string.Format` is also fine. Logging-only callsites (e.g., `_logger.LogDebug($"...")`) are tolerated because they're not stamped — but match the surrounding file's style.

### Trailing `\n` + raw bytes in the Redirector

**Always use `Encoding.UTF8.GetBytes(... + "\n")` and `Console.OpenStandardOutput().Write(bytes)`** when the Redirector returns a URL to VRChat.

**Why**: `Console.WriteLine` writes platform-specific line endings (CRLF on Windows). VRChat's async reader is line-buffered and will drop or mis-frame URLs whose newline doesn't match its expectation. `Console.Out.Write` may emit a UTF-8 BOM. We saw both bugs in the wild; raw bytes + explicit `\n` is the only thing that's been stable across VRChat updates.

**How to apply**: Don't refactor the Redirector's output path "for cleanliness". The current shape is load-bearing.

### Wrap by default; deny-list narrowly

**Every URL goes through the relay wrap unless the host is on VRChat's trusted-host list or in `NativeAvProUaHosts`.** See [[Relay Server]].

**Why**: AVPro silently rejects non-trusted hosts. Skipping the wrap "to be efficient" looks fine in tests but breaks in production for any cloud-resolver / signed-URL CDN / non-trusted host. The wrap is what makes the whole thing work.

**How to apply**: Don't widen `NativeAvProUaHosts` without reproducing the failure both ways (works pristine, fails wrapped). Don't gate the wrap on heuristics — config-only.

### No login on the native client

**WKVRCProxy.Core's tier1 strategies must not use cookies, auth tokens, or any user identity material.** The cloud (tier2) tier may use server-side credentials.

**Why**: Logged-in YouTube requests get personalised playlists, age-gated content, and recommendation contamination. They also tie a user's identity to their VRChat playback, which is a privacy footgun for shared worlds. Any cookies-based path needs to be opt-in and behind explicit flags.

**How to apply**: `yt-cookies.txt`, browser-cookie extraction, or any user-bearing token MUST be tier2-only or disabled by default. The session-cache replay used by `BrowserExtractService` is per-host transient state, not user identity — that's allowed.

### Don't revert foreign edits

If you see uncommitted changes you didn't make (a manifest you don't recognise, a UI folder you don't recognise, an orphan file in `src/`), assume parallel work is in flight and **leave it**. Add only your own files; commit only your own staged paths.

**Why**: This repo regularly has multiple workstreams running concurrently. Reverting unfamiliar edits has caused multi-hour rework before.

**How to apply**: `git add <specific paths>` rather than `git add -A`. Inspect `git status` before every commit. If unsure, ask before committing.

## Commit conventions

Subject form: `type(scope?): short summary`

`type` ∈ `feat`, `fix`, `build`, `docs`, `refactor`, `test`, `chore`. `scope` is optional; useful for cross-cutting changes (e.g., `feat(relay): …`).

`build.ps1` appends a `(YYYY.M.D.N-HASH)` build-version stamp to commit subjects automatically. **Don't paste the same stamp twice in one subject.** The `.githooks/commit-msg` hook rejects duplicates (a footgun caused by editor-template autocompletion). Bypass via `--no-verify` only when you genuinely need to.

Body explains the *why*. The diff is the *what*; don't restate it.

Every commit must pass:
- `dotnet test src/WKVRCProxy.HlsTests`
- `npm run build` in `src/WKVRCProxy.UI/ui/`
- `powershell -File build.ps1` end-to-end at least once on the branch before opening the PR (the vendor pipeline catches a class of issues `dotnet build` won't)

## Code style

Follow the surrounding file. The project deliberately avoids heavy abstraction — a new feature usually means extending an existing service, not introducing a new one.

Favour:
- Plain methods over interfaces with one impl
- `record`s for pure data
- Async/await for I/O; no fire-and-forget without an explicit reason
- Comments that explain *why* something non-obvious is the way it is

Avoid:
- New service classes "for symmetry"
- Static state in business logic (logging-only is fine)
- Mocked tests for things we can integration-test against fixtures
- Cleanup commits bundled with feature commits

## Logging

Tier discipline:
- **Debug**: useful for the maintainer when reproducing
- **Info**: a meaningful state change a user might want to see (`Strategy promoted: …`, `URL relay-wrapped on port …`)
- **Warning**: something went wrong but we recovered (`AVPro rejected resolved URL from 'X'`)
- **Error**: something the user must know about (cannot reach VRChat Tools dir, IPC bind failed)

> **No hidden tiers.** What you write to the log file goes to the UI's Logs view. Never filter UI logs to be "cleaner than the file" — that breaks bug reports. If a line is noise, delete it; don't hide it.

## Testing

`WKVRCProxy.HlsTests` covers logic that's pure-ish: HLS rewriting, yt-dlp output parsing, strategy memory ranking, version-gated wipe, trusted-host matching, etc. It does **not** cover RelayServer (HTTP integration), PatcherService (filesystem), the IPC layer, or VRChat integration.

When you add behaviour, add tests for the parts that are testable. When you change behaviour that's not testable (e.g., the patcher), add a comment explaining what manual scenario verifies it.

## Security

- See [`SECURITY.md`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/.github/SECURITY.md) for the disclosure policy.
- Vendored binaries (yt-dlp, curl-impersonate, wireproxy, etc.) are pinned in `vendor/versions.json`. Bumps go through the normal PR flow.
- Anything that touches the hosts file, patches a VRChat-owned file, or runs admin-elevated is in scope.
