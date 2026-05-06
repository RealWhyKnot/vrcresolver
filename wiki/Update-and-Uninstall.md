# Update and Uninstall

Both flows ship as standalone single-file exes that live next to `WKVRCProxy.exe`.

Sources:
- [`src/WKVRCProxy.Updater/Program.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Updater/Program.cs) -- `WKVRCProxy.Updater.exe`
- [`src/WKVRCProxy.Uninstaller/Program.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Uninstaller/Program.cs) -- `WKVRCProxy.Uninstaller.exe`
- [`.github/workflows/release.yml`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/.github/workflows/release.yml) -- produces the zip the updater consumes

## Update (`WKVRCProxy.Updater.exe`)

No flags. Running it is the request to check-and-maybe-update. Behaviour:

1. Sweeps stale `%TEMP%\WKVRCProxy-extract-*` and `WKVRCProxy-*.zip` artifacts older than 1 day from prior failed runs.
2. Reads the watchdog's `FileVersionInfo` to determine "current version".
3. GETs `https://api.github.com/repos/RealWhyKnot/WKVRCProxy/releases/latest` with a 30-second timeout, `UseDefaultCredentials=true` for corp NTLM proxies, and a `Content-Type: *json*` check (Cloudflare's "Always Online" returns 200+HTML; we surface a clearer error in that case).
4. Parses the tag, strips the leading `v` and any `-XXXX` dev suffix, parses as `System.Version`.
5. If the remote isn't newer, prints `You're on the latest version.` and exits.
6. Otherwise prompts: `Update available -- install now? [Y/N] (auto-N in 15s)`. Times out as **No** if the user is AFK.
7. On Yes:
   - Downloads the `.zip` asset with a 10-minute hard cap and a pre-download free-disk-space probe (refuses if temp drive has <1.5x the asset size free).
   - Verifies SHA256 against the release body's `^SHA256: <hex>$` line (anchored to start-of-line, multiline, so a sample placeholder elsewhere in the body can't false-match).
   - Extracts to a temp dir.
   - **Only now** does the updater stop the running watchdog -- pre-stop failures leave the running install untouched.
   - Stops the watchdog gracefully via `AttachConsole` + `GenerateConsoleCtrlEvent(CTRL_C)` so the watchdog runs its real shutdown (atomic restore + clean_exit.flag) before exit. Falls back to `Kill` after 5 s.
   - Polls "no `WKVRCProxy.exe` process AND `Global\WKVRCProxy.Watchdog` mutex acquirable" with a 5-second budget so the new watchdog launched at the end of `Main()` doesn't race the kernel's mutex-handle release.
   - Atomic two-pass copy: stages every file as `<dst>.new-<short>`, then rename pass with up to 3 retries + 200 ms backoff per file (absorbs brief AV-scanner holds). On midway failure, restores from `.old-<short>` sidecars; surfaces rollback failures in the rethrown exception with a manual-recovery hint listing each unrecovered file.
   - Lock-error messages are decorated for known critical files: a locked `yt-dlp.exe` says "close VRChat (it may be holding yt-dlp.exe)"; a locked `WKVRCProxy.exe` says "watchdog process may not have fully exited yet".
   - Relaunches the watchdog and exits.

Per-step `Logger.WriteFileOnly` breadcrumbs go to `%LOCALAPPDATA%Low\WKVRCProxy\logs\updater-<utc>.log`. A future "updater failed" report has the URL fetched, response status, asset chosen, SHA expected/actual, file count, and rollback details.

## Uninstall (`WKVRCProxy.Uninstaller.exe`)

No flags. **No prompt.** Running it IS consent. Five steps:

1. **`close-watchdog`** -- closes only `WKVRCProxy.exe` processes whose `MainModule.FileName` matches THIS install's exe (so a parallel install's watchdog isn't killed when uninstalling this one). Sends `CloseMainWindow`, falls back to `Kill` after 5 s.
2. **`restore-yt-dlp`** -- atomic move of `yt-dlp-og.exe` -> `yt-dlp.exe` in VRChat's Tools dir. Locked-target fallback: rename current to `.stale-<utc>` then move backup over. Belt-and-suspenders: if backup went missing, drops the bundled `tools/yt-dlp-og-fallback.exe` in. **Loud warning** if both backup AND bundled fallback are missing AND VRChat's `yt-dlp.exe` exists -- without intervention, VRChat would be left with our patched wrapper pointing at a soon-to-be-deleted install dir; user's told to delete `Tools/yt-dlp.exe` manually so VRChat re-downloads on next launch.
3. **`remove-hosts`** -- re-execs `WKVRCProxy.exe --remove-hosts-entry` under `runas` to remove the `127.0.0.1 localhost.youtube.com` line. Pre-checks the hosts file first; if no entry is present, skips the UAC prompt entirely (users who never enabled public-instance mode don't see a UAC dialog for a no-op write).
4. **`wipe-state`** -- releases the open log writer (`Logger.Close()`) so subsequent `Directory.Delete` doesn't hit a sharing violation, then wipes BOTH `%LOCALAPPDATA%Low\WKVRCProxy\` (current state root) AND the legacy `%LOCALAPPDATA%\WKVRCProxy\` tree (in case migration was incomplete).
5. **`schedule-self-delete`** -- detached `cmd.exe /c (ping -n 4) & rmdir /s /q "<install>"` with output redirected to `%TEMP%\WKVRCProxy-uninstall-rmdir-<utc>.log` so a stuck rmdir leaves a diagnostic trail. The 3-second delay covers AV scanners briefly holding the uninstaller's own exe handle.

Each step emits `[uninstall] <step> start / ok / ERROR` breadcrumbs to `%LOCALAPPDATA%Low\WKVRCProxy\logs\uninstaller-<utc>.log` so a "uninstall left X behind" report can identify which step failed.

## Failure modes worth knowing

- **Hardcoded GitHub URL**: the updater hits `api.github.com/repos/RealWhyKnot/WKVRCProxy`. If the repo moves, all installs need a manual update.
- **Protected install location**: installing under `Program Files` means the deferred `cmd.exe rmdir` runs without elevation and may silently fail to remove the directory. The rmdir output log at `%TEMP%\WKVRCProxy-uninstall-rmdir-<utc>.log` will show the access-denied error. Document install-anywhere usage; the README does.
- **UAC declined on uninstall**: the hosts entry stays. The uninstaller prints a hint pointing to the manual fix; the leftover line is idle and harmless without WKVRCProxy running.
- **Network down during update**: the updater never touches the running watchdog if the download/SHA/extract fail. You'll see the error message and your existing install keeps running.
