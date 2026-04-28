# Build Pipeline

`build.ps1` orchestrates the entire build: vendor pull, version stamping, UI build, .NET publish, release zip + SHA256, sidecar exes. Run from the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

## Phases

### 1. Git hooks activation (idempotent)
Sets `git config core.hooksPath = .githooks` if not already configured. Activates `commit-msg`, which rejects commit subjects with more than one `(YYYY.M.D.N-XXXX)` build-version stamp. (Common editor-template autocompletion footgun.)

### 2. Vendor pull
Each binary is pinned in `vendor/versions.json`. The fetcher:
1. Reads pinned version.
2. Skips entirely if the file exists and the version matches (the **skip-when-current** path).
3. Otherwise downloads, verifies, and refreshes both the binary and the cached version stamp.
4. On network failure, falls back to whatever is already in `vendor/` (offline-friendly).

Binaries:
| Tool | Source | Role |
|---|---|---|
| `yt-dlp.exe` | yt-dlp/yt-dlp GitHub releases | Tier1, tier3 resolution |
| `curl-impersonate-win.exe` | curl-impersonate / Windows port | Chrome TLS fingerprint; pre-flight probe + tier1:impersonate |
| `deno.exe` | denoland/deno releases | Compiles bgutil sidecar at build time |
| `bgutil-ytdlp-pot-provider.exe` | Built locally from upstream main commit via `deno compile` | YouTube PO-token sidecar |
| `streamlink/bin/streamlink.exe` | streamlink releases (portable zip) | Tier0 Twitch / live HLS |
| `wgcf.exe` | wgcf releases | Cloudflare WARP account registration |
| `wireproxy.exe` | wireproxy releases | User-space WireGuard SOCKS5 |

The bgutil sidecar uses commit-SHA tracking (not a release tag) because upstream ships from `main`. The build script clones, runs `deno install` and `deno compile`, stages the plugin tree at `vendor/yt-dlp-plugins/bgutil-ytdlp-pot-provider/`, and writes the SHA to `.version`. `BgutilPluginUpdater` (in Core) then re-checks at runtime so installed users get plugin updates without a full WKVRCProxy bump.

### 3. Version stamping
Format: `YYYY.M.D.N-HASH` (e.g., `2026.4.27.7-4974`).
- `YYYY.M.D` is today (UTC by build script convention)
- `N` is the daily counter (read from existing `dist/version.txt`)
- `HASH` is short git hash

Writes to `dist/version.txt`. Stamps the version into assembly attributes via regex over `.csproj` files. **This regex is why C# string interpolation is forbidden in stamped files** — see [[Engineering Standards]].

### 4. UI build (Vue 3 + Vite)
- `npm install` if `node_modules/` missing.
- `npm run build` → output to `src/WKVRCProxy.UI/wwwroot/`.

### 5. .NET publish
Each shipping project published self-contained, win-x64, single-file:

- `WKVRCProxy.UI` → `dist/WKVRCProxy.exe` (the desktop app)
- `WKVRCProxy.Redirector` → `dist/redirector.exe` (replaces VRChat's yt-dlp)
- `WKVRCProxy.Updater` → `dist/updater.exe` (standalone updater)
- `WKVRCProxy.Uninstaller` → `dist/uninstall.exe` (standalone uninstaller)

### 6. Tool deployment
Vendored binaries copied into `dist/tools/`:
- `dist/tools/yt-dlp.exe`, `dist/tools/curl-impersonate-win.exe`, `dist/tools/streamlink/`, etc.
- `dist/tools/yt-dlp-plugins/bgutil-ytdlp-pot-provider/` for the plugin tree
- `dist/tools/warp/` for `wgcf.exe` + `wireproxy.exe` (the WARP service writes `wgcf-account.toml`, `wgcf-profile.conf`, `wireproxy.conf` here at runtime)

### 7. Release zip + SHA256
- `release/WKVRCProxy-<version>.zip` containing the contents of `dist/`.
- `Get-FileHash -Algorithm SHA256` embedded in the GitHub release notes by `release.yml`.
- `updater.exe` parses `SHA256: <hex>` from the release body to verify the download before swapping.

### 8. Strategy-memory wipe on version change
If `strategy_memory.json` exists and its embedded version doesn't match `dist/version.txt`, the file is wiped before the build's first run. This prevents stale learned rankings from surviving logic changes (e.g., a strategy gets reordered or renamed). Dev runs without `version.txt` are exempt — `dotnet run` against source shouldn't lose iteration state.

## CI pipeline

`.github/workflows/ci.yml` runs on push and PR to `main`:

- Single `windows-latest` job (`net10.0-windows` requires Windows runner)
- `actions/setup-dotnet@v5` with `10.0.x`
- Restores **only** `src/WKVRCProxy.HlsTests` (the slnx excludes it; restoring the slnx skips it)
- Builds + tests in Release configuration
- Uploads `test-results/*.trx`

CI **deliberately skips `build.ps1`** — the vendor pull is heavy (7 binaries from various sources) and would make CI slow + flaky. The test suite is the meaningful gate.

## Release pipeline

`.github/workflows/release.yml` runs on tag push matching `v*` (e.g., `v2026.4.27.7-4974`):

- `windows-latest`, full `build.ps1` execution
- Promotes the `## Unreleased` section in `CHANGELOG.md` and `wiki/Changelog.md` to the tagged version (see [[#Changelog automation]]) before the build runs, so the embedded copy in the exe carries that version's notes.
- Locates `release/WKVRCProxy-*.zip`, computes SHA256
- `gh release create $tag $zip --title $tag --notes "...SHA256: <hash>...<promoted notes>..."`
- Opens a `release/promote-changelog-<tag>` branch with the promoted changelog files, opens a PR against `main`, and enables auto-merge (`gh pr merge --auto --squash --delete-branch`). Once CI passes the PR auto-merges and `main` carries the promotion. Direct push doesn't work — branch protection requires the `dotnet build + test` check, which a bot push bypasses (so the push is rejected).

Tag format must match the version stamp `build.ps1` produces, with a leading `v`.

## Changelog automation

The changelog is autogenerated from commit subjects on `main` — **don't hand-edit `CHANGELOG.md` or `wiki/Changelog.md` under `## Unreleased`**, those edits will be overwritten on the next push.

How it works:

- **`.github/workflows/changelog-append.yml`** runs on every push to `main`. It walks the commits in the push range, parses each subject as a [conventional commit](https://www.conventionalcommits.org/), and appends bullets under `## Unreleased` in both files. `feat:` → Added, `fix:` → Fixed, `perf:`/`refactor:`/`revert:`/`chore(deps):` → Changed; subjects with `!` go under Breaking. `docs:`/`build:`/`ci:`/`test:`/non-deps `chore:` are skipped (real work, but not user-visible release notes). The bot then commits and force-triggers `wiki-sync.yml` so the GitHub Wiki mirrors the new state.
- **`.github/workflows/release.yml`** runs on tag push. Before `build.ps1`, it renames `## Unreleased` to `## [vTAG] - DATE` (linked to the GitHub release page) and inserts a fresh empty `## Unreleased` above. The build then embeds the promoted `CHANGELOG.md` into the exe via `WKVRCProxy.UI.csproj <EmbeddedResource>` so the in-app viewer shows the tagged section. The promoted file content is also injected into the GitHub release notes between the SHA256 line and the asset list. After the release is published, the workflow opens a `release/promote-changelog-<tag>` PR with auto-merge enabled so `main` eventually carries the promotion (squashed in once CI clears) — `ci.yml` carves `CHANGELOG.md` and `wiki/Changelog.md` out of `paths-ignore` (via `!`-negation) so the required check actually fires on changelog-only PRs.
- Both workflows share a concurrency group, so the appender can never race with a release-in-progress and add entries to `main`'s Unreleased that the released exe doesn't contain.

Overrides:

- To skip a commit from the changelog: include `[skip changelog]` anywhere in the subject.
- To override an entry's wording: amend the commit subject before merge. Once a commit is on `main`, the bullet is pinned to the short SHA — re-pushing won't re-process.
- To hand-edit a tagged section retroactively: edit either `CHANGELOG.md` or `wiki/Changelog.md` (or both — they should match) under the existing `## [vX]` heading. Hand edits to *closed* sections aren't touched by the workflow; only `## Unreleased` is automated.

Implementation lives in `.github/scripts/Update-Changelog.ps1` (PowerShell Core, callable from both runners). Modes: `Append` (parse commit range), `Promote` (rename Unreleased to versioned), `Notes` (read a section to stdout).
