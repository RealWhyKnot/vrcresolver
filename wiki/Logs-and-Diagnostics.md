# Logs and Diagnostics

Where every log file lives + what to attach when filing a bug.

## Log files

All under `%LOCALAPPDATA%Low\WKVRCProxy\` (LocalLow because the patched yt-dlp wrapper runs at Low integrity and needs to write here too):

| File | What's in it | When to attach |
| --- | --- | --- |
| `logs\watchdog-<utc>.log` | Full watchdog console output (stdout AND stderr both teed). Patch ticks, mesh state, hosts ticks, heartbeat, per-resolve summaries, shutdown trace. | Any bug involving the watchdog itself, mesh disconnects, "loading failed" reports, patch behavior. |
| `logs\yt-dlp-wrapper.log` | One log block per `Tools/yt-dlp.exe` invocation by VRChat. Each block keyed by `[<utc>] [<rid>]` correlation id: pipe connect / request bytes / response received / og fallback exec + stdout/stderr previews / END summary. | "VRChat couldn't load this video" -- the wrapper's view tells you whether the pipe path or og fallback was used and what came back. |
| `logs\updater-<utc>.log` | Per-step trace from `WKVRCProxy.Updater.exe`: GET status, asset chosen, SHA expected/actual, download bytes, file copy outcomes. | Updater failures or hangs. |
| `logs\uninstaller-<utc>.log` | `[uninstall] <step> start / ok / ERROR` breadcrumbs for each of the 5 uninstall steps. | "Uninstaller left X behind" -- identifies which step failed. |
| `crashes\crash-<component>-<utc>.log` | Unhandled-exception postmortems. Carries timestamp, component, exception type + stack trace, PID, version, OS, optional state-snapshot (mesh state, patch state, pending-request count). `%USERPROFILE%` and your username are redacted before write. | Crash reports -- these have everything a maintainer needs. |

**Rotation**: each file is opened fresh per process start. Watchdog/Updater/Uninstaller logs rotate at 10 MiB; files older than 7 days are pruned at the next launch. The wrapper log is append-only (one shared file across all wrapper invocations); manual cleanup is fine if it gets large but typical sessions stay under 1 MiB.

## State files (NOT logs)

Same root, no `logs/` prefix:

| File | Meaning |
| --- | --- |
| `clean_exit.flag` | Watchdog wrote this on graceful shutdown. Absent on next launch => watchdog runs unclean-shutdown recovery (sweep stale sidecars, restore yt-dlp from og if patched build was left in place). |
| `halt.flag` | Patcher halted with a fatal reason. Watchdog refuses to re-engage until the user reinstalls. |
| `codec-state.json` | `CodecInstaller`'s per-codec install attempt log. 7-day backoff after a failed attempt. |
| `yt-dlp-update-check.json` | 24-hour dedupe state for the `YtDlpUpdater`'s background check (the bundled vanilla yt-dlp at `tools/yt-dlp-og-fallback.exe`, NOT the patched wrapper). |
| `.migrated-from-localapp` | Marker -- presence skips re-running the legacy state migration on every launch. |

## VRChat-side files (for cross-correlation)

| Path | Purpose |
| --- | --- |
| `%LOCALAPPDATA%Low\VRChat\VRChat\Tools\yt-dlp.exe` | Should be our patched wrapper (~3.27 MB AOT). If it's larger or different, either the patcher hasn't engaged yet or VRChat overwrote it and the watchdog hasn't re-applied. |
| `%LOCALAPPDATA%Low\VRChat\VRChat\Tools\yt-dlp-og.exe` | Should be VRChat's vanilla yt-dlp, preserved by the patcher. The patched wrapper execs this on fallback. |
| `%LOCALAPPDATA%Low\VRChat\VRChat\output_log_*.txt` | VRChat's own log. AVPro-side errors (`[AVProVideo] Error: Loading failed.`), `playback_feedback`-eligible events. |

## Filing a bug

1. Reproduce the failure.
2. Grab the latest `watchdog-<utc>.log` and (if the failure was during a video play) `yt-dlp-wrapper.log` from `%LOCALAPPDATA%Low\WKVRCProxy\logs\`.
3. If the watchdog crashed, grab the `crash-watchdog-<utc>.log` from `%LOCALAPPDATA%Low\WKVRCProxy\crashes\`.
4. Open a [bug report](https://github.com/RealWhyKnot/WKVRCProxy/issues/new?template=bug_report.yml) and attach. The crash log is already redacted; the watchdog log may contain the URLs you tried to play (we sanitize for tokens but the host is visible).

## Resolve summary format

Two coloured lines per resolve in the watchdog console (also in `watchdog-<utc>.log`):

```
[14:32:10]  -> youtube.com  (AVPro 1080p)              <- cyan, request received
[14:32:13]     OK resolved  3.0s                        <- green, mesh succeeded
```

Other terminal-line variants:

```
[14:32:13]     !! fallback (discovery_in_progress)  10.1s    <- yellow, server replied fallback_native
[14:32:13]     XX failed (server_unreachable)  15.0s         <- red, watchdog synthesised fallback (mesh down)
```

A `[via lh-yt]` tag on the request line indicates the URL came in via the `localhost.youtube.com` trust-list bypass.

## Heartbeat

Every 30 minutes when nothing else has logged in the last 5 minutes:

```
[heartbeat] up=2h13m mesh=connected resolves=47 (3 via lh-yt) stream-bytes=1.2 GB reconnects=0
```

Suppressed when activity is recent -- silence is preferred over noise. The stats are session-scoped (reset to zero on each watchdog start; not persisted).
