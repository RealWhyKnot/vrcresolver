# Update and Uninstall

Both flows ship as standalone single-file exes that live next to `WKVRCProxy.exe`.

Sources:
- [`src/WKVRCProxy.Updater/Program.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Updater/Program.cs) — `WKVRCProxy.Updater.exe`
- [`src/WKVRCProxy.Uninstaller/Program.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Uninstaller/Program.cs) — `WKVRCProxy.Uninstaller.exe`
- [`.github/workflows/release.yml`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/.github/workflows/release.yml) — produces the zip the updater consumes

## Update (`WKVRCProxy.Updater.exe`)

No flags. Running it is the request to check-and-maybe-update. Behaviour:

1. Reads the watchdog's `FileVersionInfo` to determine "current version".
2. GETs `https://api.github.com/repos/RealWhyKnot/WKVRCProxy/releases/latest`.
3. Parses the tag, strips the leading `v`, parses as `System.Version`.
4. If the remote isn't newer, prints `You're on the latest version.` and exits.
5. Otherwise prompts: `Update available — install now? [Y/N] (auto-N in 15s)`. Times out as **No** if the user is AFK.
6. On Yes:
   - Closes the running watchdog (`CloseMainWindow` → `Kill` after 5 s).
   - Downloads the `.zip` asset from the release assets.
   - Extracts to a temp dir, copies files over the install dir (skipping its own exe so it doesn't try to overwrite itself while running).
   - Relaunches the watchdog and exits.

## Uninstall (`WKVRCProxy.Uninstaller.exe`)

No flags. **No prompt.** Running it IS consent. Behaviour:

1. Closes any running `WKVRCProxy.exe` (`CloseMainWindow` → `Kill` if needed).
2. Restores VRChat's `yt-dlp.exe` from `yt-dlp-og.exe`. Belt-and-suspenders: if the backup went missing, drops the bundled vanilla yt-dlp in place so VRChat still has a working `yt-dlp.exe`.
3. Re-execs `WKVRCProxy.exe --remove-hosts-entry` under `runas` to remove the `127.0.0.1 localhost.youtube.com` line.
4. Wipes `%LOCALAPPDATA%\WKVRCProxy\`.
5. Schedules the install directory's self-deletion via a detached `cmd.exe /c rmdir`, so the uninstaller's own exe isn't locked when the directory disappears.
6. Prints a single completion line and exits with status 0 (or non-zero if any step had errors).

## Failure modes worth knowing

- **Hardcoded GitHub URL**: the updater hits `api.github.com/repos/RealWhyKnot/WKVRCProxy`. If the repo moves, all installs need a manual update.
- **Protected install location**: installing under `Program Files` means the deferred `cmd.exe rmdir` runs without elevation and may silently fail to remove the directory. Document install-anywhere usage; the README does.
- **UAC declined on uninstall**: the hosts entry stays. The uninstaller prints a hint pointing to the manual fix; the leftover line is idle and harmless without WKVRCProxy running.
