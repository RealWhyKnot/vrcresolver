# Update and Uninstall

Both flows ship as standalone single-file exes that live next to `WKVRCProxy.exe`. Each can run when WKVRCProxy itself isn't, so they don't share its IPC; they load what they need from `WKVRCProxy.Core` directly.

Sources:
- [`src/WKVRCProxy.Core/Services/AppUpdateChecker.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/AppUpdateChecker.cs) — periodic version poll
- [`src/WKVRCProxy.Updater/Program.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Updater/Program.cs) — `updater.exe`
- [`src/WKVRCProxy.Uninstaller/Program.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Uninstaller/Program.cs) — `uninstall.exe`
- [`.github/workflows/release.yml`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/.github/workflows/release.yml) — produces the zip+SHA256 the updater consumes

## Update poll (`AppUpdateChecker`)

In-process, runs alongside the engine.

1. Reads local version from `version.txt`.
2. GETs `https://api.github.com/repos/RealWhyKnot/WKVRCProxy/releases/latest`.
3. Parses the tag (`tag_name`), strips the leading `v`, parses as `System.Version`.
4. If remote > local, fires `OnStatusChanged(UpdateStatus.UpdateAvailable, releaseUrl)`.
5. The UI shows a banner + modal in `App.vue`; **Settings → Maintenance** has a manual **Check now** button.

`AppUpdateChecker` does **not** download or apply. It only signals.

## Update apply (`updater.exe`)

Triggered by the user clicking **Update now** in the UI. The UI sends a `LAUNCH_UPDATER` IPC, the bridge spawns `updater.exe`, and the UI begins shutdown.

1. Updater verifies `WKVRCProxy.exe` exists in its install dir.
2. Reads current version from `version.txt`.
3. Fetches the latest release JSON.
4. If versions match, relaunches and exits (nothing to do).
5. **Parses `SHA256: <hex>` from the release notes** and refuses to proceed if it's missing or malformed (exit code 12). The value is captured here so a malformed release surfaces *before* the user is asked to close VRChat.
6. Polls the single-instance mutex `Local\WKVRCProxy.UI.SingleInstance` for up to 30 s, waiting for the UI to fully exit.
7. Downloads `WKVRCProxy-<version>.zip` to a temp directory.
8. **Verifies the downloaded zip's SHA256 matches** the value parsed in step 5. Mismatch is a hard fail (exit code 7) — the zip is never extracted.
9. Extracts to a staging temp dir, **rejecting any entry whose resolved path escapes the staging dir** (Zip Slip guard, exit code 13). If the zip has a single top-level folder (likely), unwraps it.
10. Renames the existing install dir aside as `<install>-old-<timestamp>`.
11. Moves the staged payload into place.
12. Schedules the old dir for delete via `cmd.exe /c timeout 3 && rmdir /s /q "<old>"`.
13. Relaunches `WKVRCProxy.exe` from the new install dir, exits.

The atomic-swap means a failed move (locked file, AV interference) leaves the old install in place and recoverable. The updater logs to `updater.log` in the install dir.

## Uninstall (`uninstall.exe`)

Triggered from **Settings → Maintenance → Uninstall WKVRCProxy** (with confirm dialog). The UI sends `LAUNCH_UNINSTALLER`, spawns `uninstall.exe`, and exits.

1. Native confirm dialog (WinForms): explains exactly what gets deleted.
2. Closes WKVRCProxy gracefully (`CloseMainWindow` then force-kill after 5 s).
3. Polls the single-instance mutex (10 s timeout).
4. Reads `customVrcPath` from `settings.json` if present and valid; else uses `VrcPathLocator.Find(null)`.
5. Calls `PatcherService.RestoreYtDlpInTools()` to revert VRChat's `yt-dlp.exe` from `yt-dlp-og.exe`.
6. Deletes `%LOCALAPPDATA%\WKVRCProxy\`.
7. Schedules install dir delete via `cmd.exe /c timeout 2 && rmdir /s /q "<install>"` (delayed so `uninstall.exe` itself isn't holding any of those files).
8. Shows a success dialog, exits.

What it does **not** touch:
- The hosts file entry `127.0.0.1 localhost.youtube.com`. Idle and harmless; users can remove it by hand.
- `app_config.json` and `strategy_memory.json` — they live next to the exe and go with the install dir delete.

## Failure modes worth knowing

- **Hardcoded GitHub URL**: both `AppUpdateChecker` and the updater hit `api.github.com/repos/RealWhyKnot/WKVRCProxy`. If the repo moves, all installs need a manual update.
- **Protected install location**: if WKVRCProxy is installed in `Program Files`, the deferred `cmd.exe rmdir` runs without elevation and may silently fail. Document install-anywhere usage in the README.
- **No SHA256 in release notes**: the updater refuses to proceed (exit code 12). `release.yml` always emits `SHA256: <hex>` from `Get-FileHash`, so this only happens if a release is published by another path. Add the line manually to the release body and the next update attempt will succeed.
- **Zip with path-traversal entries**: the updater rejects the whole update (exit code 13) rather than extracting a partial payload. Should never trip on a `release.yml`-produced zip; if it does, treat as a tampering signal.
- **Updater + UI version mismatch**: the updater binary lives next to the UI, so they bump together. If a user manually replaces `updater.exe` from a different release, behaviour is undefined.
