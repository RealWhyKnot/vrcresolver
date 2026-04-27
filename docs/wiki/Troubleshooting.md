# Troubleshooting

## Reading the Logs view

Every resolution gets a correlation ID like `[a1b2c3d4]`. Filter the Logs tab by that ID to see one URL's full lifecycle, from `Starting resolution for: …` through tier decisions, relay-wrap, pre-flight probe, and either a successful return or the demote path.

Useful patterns to grep:

| Substring | Meaning |
|---|---|
| `Starting resolution for: …` | Engine accepted a URL from the Redirector |
| `Final Resolution [tierN] [vod\|live] in Xms` | Successful resolve |
| `URL relay-wrapped on port …` | Wrap applied as expected |
| `Relay wrap skipped for <host>` | Either trusted host or `NativeAvProUaHosts` |
| `[WARNING] [Playback] AVPro rejected resolved URL from 'X'` | Demotion fired; correlation ID identifies which request |
| `Strategy demoted: <name>` | StrategyMemory recorded a failure |
| `Egress IP via WARP: …` | WarpService verified WARP came up |

## Common failures

### "Loading failed" but the URL works in a browser

Almost always either:
- The host isn't in VRChat's trusted-host list **and** the relay wrap is being skipped (check for `Relay wrap skipped`). If unexpected, look at `NativeAvProUaHosts` in `app_config.json`.
- The hosts-file entry is missing — check `C:\Windows\System32\drivers\etc\hosts` for `127.0.0.1 localhost.youtube.com`. If absent, restart WKVRCProxy and accept the UAC prompt.
- The pre-flight probe failed silently (network blip; the engine returned the URL anyway and AVPro hit the same issue). Look for `Pre-flight probe failed` near the correlation ID.

### YouTube videos play but Twitch doesn't

Twitch live needs `tier0:streamlink`. Check that `dist/tools/streamlink/bin/streamlink.exe` exists. If `tier0` is in `disabledTiers`, it's been turned off by the user.

### A previously-working host suddenly fails after an app update

`strategy_memory.json` is wiped on version change to avoid stale rankings. The first request to each host re-cascades from cold; expect a few seconds longer than usual until memory rebuilds.

### Patcher won't apply (yt-dlp.exe in Tools is locked)

VRChat is probably still running. The patcher is idempotent — the next monitor tick (every 3 s) will retry. If it persists, close VRChat fully and check Task Manager for stray `yt-dlp.exe` or `redirector.exe` processes.

### Hosts file UAC declined

`bypassHostsSetupDeclined: true` is now in `app_config.json` — the prompt won't return. To re-prompt: set it back to `false`, restart WKVRCProxy. Or add the line manually with admin Notepad.

### WARP strategies never fire / "WARP unavailable"

`wgcf register` may have failed (Cloudflare temporarily rejecting registrations from your IP). Try again later; or delete `dist/tools/warp/wgcf-account.toml` and restart so registration retries. Verify `wgcf.exe` and `wireproxy.exe` exist in `dist/tools/`.

### Browser-extract strategy never fires

Either no browser is found (Edge / Chrome not installed; `downloadBundledChromium` is `false`) or `enableBrowserExtract` is `false`. The Logs view will say `BrowserExtractService disabled: no browser available` at startup.

### Updater fails to swap

Usually file locking — antivirus, indexing, or a stray `updater.exe` from a previous run. Check `updater.log` next to the install dir. The old install is renamed aside as `<install>-old-<timestamp>` and is recoverable: copy contents back if needed.

## Where to look first

1. **Logs view** in the UI — correlation-ID block of the failing request
2. **`wkvrcproxy_<timestamp>.log`** next to the exe — same content, plus debug-level lines if `debugMode: true`
3. **VRChat output log** — `%LOCALAPPDATA%Low\VRChat\VRChat\output_log_*.txt` for the AVPro side of the conversation
4. **`yt-dlp-wrapper.log`** in VRChat's Tools dir — what the Redirector saw
5. **`app_config.json`** — what was configured, especially `disabledTiers`, `nativeAvProUaHosts`, `enableRelayBypass`

## Filing a bug

Use the [bug report template](https://github.com/RealWhyKnot/WKVRCProxy/issues/new?template=bug_report.yml). Paste the correlation-ID block from the Logs view verbatim — that's the difference between "we'll figure it out" and "we can't help from here."
