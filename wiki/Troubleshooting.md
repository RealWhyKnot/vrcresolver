# Troubleshooting

## Reading the watchdog console

The watchdog prints to its console window. Useful lines:

| Substring | Meaning |
| --- | --- |
| `Patch applied. Watching for VRChat overwrites -- Ctrl+C to quit.` | Watchdog up, patched yt-dlp in place |
| `[relay] listening port {port}` | Trust-gateway HTTP listener bound; the wrapper will route resolved URLs through `localhost.youtube.com:{port}` so AVPro accepts them in default-public worlds |
| `[mesh] connected` | Persistent WS to proxy.whyknot.dev established |
| `[mesh] disconnected -- <reason>` | WS dropped; resolves are falling back to vanilla yt-dlp until the next reconnect |
| `[mesh] reconnect attempt N in M s` | Backoff window before next reconnect |
| `[patch] yt-dlp.exe was overwritten -- re-applied.` | VRChat replaced the patched file (typical at launch); watchdog restored within 3 s |
| `yt-dlp-og.exe was missing -- restored from bundled fallback (vNNN).` | Backup went missing; the bundled vanilla yt-dlp was dropped in to keep the patched build's fallback path functional |
| `Cannot apply patch -- VRChat hasn't shipped its own yt-dlp.exe yet...` | Launch VRChat once and re-run the watchdog |

## Common failures

### "Loading failed" in VRChat

When the watchdog isn't running or can't reach the server, the patched `yt-dlp.exe` falls back to vanilla yt-dlp. So a `Loading failed` you'd see *with* the watchdog is also a failure you'd see *without* it. Check:

- Is the watchdog window open and showing `[mesh] connected`?
- Did UAC get declined when adding the hosts entry? It's the public-instance trust-list bypass -- re-run `WKVRCProxy.exe` and accept the prompt, or add the line manually with admin Notepad: `127.0.0.1 localhost.youtube.com`. The watchdog's `[hosts]` ticker will re-prompt on next launch if you decline now.

### Trust gateway didn't bind

Watchdog log shows `[relay][error] HttpListener.Start failed on port {port}` or `[relay][warn] could not allocate ephemeral port`. The trust gateway's local HTTP listener didn't come up; the port file was deleted, and the wrapper is falling through to emitting raw server URLs (today's behavior in pre-trust-gateway worlds; works in trust-disabled worlds, fails in default-public).

- Most common cause: another process is bound to 127.0.0.1 on the port the watchdog tried. Restart the watchdog -- it picks a different ephemeral port on each launch.
- If `[relay][warn] could not allocate ephemeral port` appears: Windows refused to allocate any ephemeral port at all. Rare. Check `netstat -an` for exhaustion or restart the OS.
- Symptom in the wrapper trace (`yt-dlp-wrapper.log`): every resolve shows `trust_gateway=passthrough` instead of `trust_gateway=wrapped`. Confirms the wrapper read the missing port file and fell through.

The full Trust-Gateway flow is documented at [[Trust Gateway]].

### Public-instance worlds still reject the URL

Hosts entry got removed since the watchdog last ticked (manual edit, AV rewrite, OS rollback). The `HostsTicker` re-adds within 60 seconds; wait or restart the watchdog. Check `%LOCALAPPDATA%Low\WKVRCProxy\logs\watchdog-*.log` for `[hosts] tick: localhost.youtube.com entry MISSING -- re-adding via UAC re-exec.` If UAC was declined, the entry stays missing until the next launch + accept.

### Watchdog won't replace yt-dlp.exe -- VRChat is running

If VRChat is open when you start the watchdog, the patcher prints something like `[patch] yt-dlp.exe in use by another process -- deferring` and waits. Close VRChat (or the Camera capture window if you used "Stop Recording" but the process is still up) and the patcher will engage on the next 3-second tick.

This is intentional -- swapping `yt-dlp.exe` while VRChat has it open could crash VRChat. The patcher uses a `FileShare.None` probe to detect the lock and waits the user out.

### Watchdog refuses to apply the patch

Console says `Cannot apply patch -- VRChat hasn't shipped its own yt-dlp.exe yet, and we have no original to preserve as fallback.` -- VRChat hasn't created its `Tools/yt-dlp.exe` yet. Launch VRChat once (let it sit at the start screen) and re-run the watchdog.

### "Reinstall WKVRCProxy"

Defensive halt: both the patched yt-dlp and the bundled fallback are missing from this install. Re-extract the release zip.

### Watchdog can't reach the server

`[mesh] disconnected` followed by reconnect attempts. The watchdog connects to `proxy.whyknot.dev` first and can fall back to the legacy apex discovery path if the proxy host is unavailable before a connection is established. While disconnected, the patched `yt-dlp.exe` execs the vanilla `yt-dlp-og.exe` and you get vanilla yt-dlp behaviour -- playback works, but without the server-assisted resolution.

If reconnects never succeed, check that `proxy.whyknot.dev` and `whyknot.dev` resolve and are reachable from your network.

### Hosts entry won't take

The `127.0.0.1 localhost.youtube.com` line goes into `C:\Windows\System32\drivers\etc\hosts`. If UAC was declined, the watchdog logs a hint and continues without it. The `HostsTicker` re-checks every minute; if the entry is still missing 10 minutes later, it'll re-prompt for UAC (rate-limited so a user who keeps declining doesn't get spammed). To add by hand, open Notepad as administrator, edit the file, append the line, save.

### Uninstall didn't remove the install dir

The detached `cmd.exe /c rmdir` runs after the uninstaller exits. If the install dir is under `Program Files`, the rmdir runs unelevated and may silently fail. Move the install elsewhere (anywhere outside `Program Files`) and re-run, or remove the directory by hand.

## Where to look first

1. **`%LOCALAPPDATA%Low\WKVRCProxy\logs\watchdog-<utc>.log`** -- full watchdog output, captures everything the console printed (and stderr too). Open the most recent file.
2. **`%LOCALAPPDATA%Low\WKVRCProxy\logs\yt-dlp-wrapper.log`** -- per-invocation trace from the patched yt-dlp wrapper, one line block per video play. Each block is keyed by an `[<utc>] [<rid>]` correlation id so you can match the wrapper's view of a resolve to the watchdog's.
3. **`%LOCALAPPDATA%Low\WKVRCProxy\crashes\crash-<component>-<utc>.log`** -- unhandled-exception postmortems. `%USERPROFILE%` and your username are redacted before write so it's safe to attach to bug reports.
4. **`%LOCALAPPDATA%Low\WKVRCProxy\clean_exit.flag`** -- present means the previous run shut down cleanly; absent means recovery on next launch.
5. **VRChat output log** -- `%LOCALAPPDATA%Low\VRChat\VRChat\output_log_*.txt` for the AVPro side.
6. **`%LOCALAPPDATA%Low\VRChat\VRChat\Tools\`** -- `yt-dlp.exe` should be the watchdog's patched wrapper (~3.27 MB AOT); `yt-dlp-og.exe` should be VRChat's vanilla copy. No sidecars (`.new-*`, `.stale-*`) should remain after a clean shutdown.

## Filing a bug

Use the [bug report template](https://github.com/RealWhyKnot/WKVRCProxy/issues/new?template=bug_report.yml). Include the watchdog console output around the failure verbatim.
