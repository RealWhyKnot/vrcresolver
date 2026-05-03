# Troubleshooting

## Reading the watchdog console

The watchdog prints to its console window. Useful lines:

| Substring | Meaning |
| --- | --- |
| `Patch applied. Watching for VRChat overwrites — Ctrl+C to quit.` | Watchdog up, patched yt-dlp in place |
| `[mesh] connected` | Persistent WS to whyknot.dev established |
| `[mesh] disconnected — <reason>` | WS dropped; resolves are falling back to vanilla yt-dlp until the next reconnect |
| `[mesh] reconnect attempt N in M s` | Backoff window before next reconnect |
| `[patch] yt-dlp.exe was overwritten — re-applied.` | VRChat replaced the patched file (typical at launch); watchdog restored within 3 s |
| `yt-dlp-og.exe was missing — restored from bundled fallback (vNNN).` | Backup went missing; the bundled vanilla yt-dlp was dropped in to keep the patched build's fallback path functional |
| `Cannot apply patch — VRChat hasn't shipped its own yt-dlp.exe yet…` | Launch VRChat once and re-run the watchdog |

## Common failures

### "Loading failed" in VRChat

When the watchdog isn't running or can't reach the server, the patched `yt-dlp.exe` falls back to vanilla yt-dlp. So a `Loading failed` you'd see *with* the watchdog is also a failure you'd see *without* it. Check:

- Is the watchdog window open and showing `[mesh] connected`?
- Did UAC get declined when adding the hosts entry? Public-instance support depends on it. Re-run `WKVRCProxy.exe` and accept the prompt, or add the line manually with admin Notepad: `127.0.0.1 localhost.youtube.com`.

### Watchdog refuses to apply the patch

Console says `Cannot apply patch — VRChat hasn't shipped its own yt-dlp.exe yet, and we have no original to preserve as fallback.` — VRChat hasn't created its `Tools/yt-dlp.exe` yet. Launch VRChat once (let it sit at the start screen) and re-run the watchdog.

### "Reinstall WKVRCProxy"

Defensive halt: both the patched yt-dlp and the bundled fallback are missing from this install. Re-extract the release zip.

### Watchdog can't reach the server

`[mesh] disconnected` followed by reconnect attempts. The watchdog re-resolves the apex node every 5 minutes of continuous failure. While disconnected, the patched `yt-dlp.exe` execs the vanilla `yt-dlp-og.exe` and you get vanilla yt-dlp behaviour — playback works, but without the server-assisted resolution.

If reconnects never succeed, check that `whyknot.dev` resolves and is reachable from your network.

### Hosts entry won't take

The `127.0.0.1 localhost.youtube.com` line goes into `C:\Windows\System32\drivers\etc\hosts`. If UAC was declined, the watchdog logs a hint and continues without it. To add by hand, open Notepad as administrator, edit the file, append the line, save.

### Uninstall didn't remove the install dir

The detached `cmd.exe /c rmdir` runs after the uninstaller exits. If the install dir is under `Program Files`, the rmdir runs unelevated and may silently fail. Move the install elsewhere (anywhere outside `Program Files`) and re-run, or remove the directory by hand.

## Where to look first

1. **Watchdog console** — most failures are visible there
2. **`%LOCALAPPDATA%\WKVRCProxy\clean_exit.flag`** — present means the previous run shut down cleanly; absent means recovery on next launch
3. **VRChat output log** — `%LOCALAPPDATA%Low\VRChat\VRChat\output_log_*.txt` for the AVPro side
4. **`%LOCALAPPDATA%Low\VRChat\VRChat\Tools\`** — `yt-dlp.exe` should hash-equal the watchdog's `tools/yt-dlp-patched.exe`; `yt-dlp-og.exe` should be VRChat's vanilla copy

## Filing a bug

Use the [bug report template](https://github.com/RealWhyKnot/WKVRCProxy/issues/new?template=bug_report.yml). Include the watchdog console output around the failure verbatim.
