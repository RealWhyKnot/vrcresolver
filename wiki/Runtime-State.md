# Runtime State

Files WKVRCProxy reads or writes outside source control.

## Next to the exe

For release builds, "next to the exe" is `dist/`. For dev runs, it's `src/WKVRCProxy.UI/bin/Debug/net10.0-windows/`.

| File | Purpose | Lifetime |
|---|---|---|
| `version.txt` | Build version stamp `YYYY.M.D.N-HASH` | Set by `build.ps1`. Read by `AppUpdateChecker`, `updater.exe`, and the strategy-memory wipe check. |
| `app_config.json` | User settings — every field in [[Settings Reference]] | Persisted; survives version changes. |
| `strategy_memory.json` | Per-host/strategy W/L rankings | **Wiped on version change.** See [[Resolution Cascade]]. |
| `proxy-rules.json` | Per-domain relay behaviour (header forwarding, UA override, PO-token injection, curl-impersonate toggle) | Persisted; user edits preserved. |
| `wkvrcproxy_<timestamp>.log` | Per-session log with correlation IDs | Per-session. Old files accumulate — clean up manually when needed. |

## `%LOCALAPPDATA%\WKVRCProxy\`

| File | Purpose |
|---|---|
| `ipc_port.dat` | IPC server port (Redirector reads this) |
| `relay_port.dat` | Relay server port |
| `clean_exit.flag` | Touched on graceful shutdown. Missing on next launch ⇒ probable crash, recovery flow runs. |

This whole directory is deleted by the Uninstaller. Wiping it manually resets all learned strategy memory and any cached runtime ports without affecting `app_config.json` (which lives next to the exe).

## VRChat's Tools directory

Default: `%LOCALAPPDATA%Low\VRChat\VRChat\Tools\`. Override via `customVrcPath` in settings.

| File | Owner | Purpose |
|---|---|---|
| `yt-dlp.exe` | WKVRCProxy (when patched) | Replaced with `redirector.exe`. VRChat calls this when resolving URLs. |
| `yt-dlp-og.exe` | WKVRCProxy | Backup of VRChat's original `yt-dlp.exe`. Written on first patch. |
| `ipc_port.dat` | WKVRCProxy | Mirror of the AppData copy so the Redirector doesn't have to look up AppData. |
| `relay_port.dat` | WKVRCProxy | Same. |
| `yt-dlp-wrapper.log` | Redirector | Subprocess-level log (one file per VRChat session). |

The Uninstaller restores `yt-dlp-og.exe` → `yt-dlp.exe` and removes the WKVRCProxy-owned files.

## `dist/tools/` (vendored binaries)

Shipped with the install. See [[Build Pipeline]] for what's in there and where each comes from.

The `warp/` subfolder gets three runtime-generated files on first WARP use:

| File | Purpose | Lifetime |
|---|---|---|
| `wgcf-account.toml` | Cloudflare WARP account token | Created once per install by `wgcf register`; reused forever. |
| `wgcf-profile.conf` | WireGuard config from `wgcf generate` | Persisted. |
| `wireproxy.conf` | wireproxy config (the WireGuard config + a `[Socks5]` section appended by WarpService) | Persisted. |

## System hosts file

`%WINDIR%\System32\drivers\etc\hosts`:

```
127.0.0.1 localhost.youtube.com
```

Added by `HostsManager` with UAC on first run. Persists across uninstalls unless the user manually removes it. The Uninstaller doesn't remove it (would require a second UAC prompt and there's no harm in leaving an idle 127.0.0.1 mapping).

## Registry

None. WKVRCProxy doesn't write to HKLM or HKCU. All state is files.

## Auto-cleanup

- `clean_exit.flag` reset on graceful shutdown; checked at next launch (presence ⇒ clean prior session).
- Streamlink negative-cache: 24 h TTL for "can't handle"; 7 d TTL for "can handle".
- Per-domain PO requirement flag: 30 min TTL.
- Resolve cache: per-tier TTL (cloud entries shorter than local).
