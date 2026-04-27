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
- Locates `release/WKVRCProxy-*.zip`, computes SHA256
- `gh release create $tag $zip --title $tag --notes "...SHA256: <hash>..."`

Tag format must match the version stamp `build.ps1` produces, with a leading `v`.
