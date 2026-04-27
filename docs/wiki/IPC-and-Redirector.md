# IPC and Redirector

VRChat's video pipeline doesn't know WKVRCProxy exists. The integration point is `yt-dlp.exe` itself — VRChat ships one in `Tools/`, and we replace it.

## Patcher flow

Source: [`PatcherService.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/PatcherService.cs), [`VrcPathLocator.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/VrcPathLocator.cs).

1. **Locate VRChat's Tools dir** — `VrcPathLocator.Find(customPath)`:
   - If `customPath` from settings exists, return it.
   - Else `%LOCALAPPDATA%Low\VRChat\VRChat\Tools`.
   - Else null (PatcherService surfaces a UI error so the user can pick a path).
2. **Back up the original** — copy `Tools\yt-dlp.exe` to `Tools\yt-dlp-og.exe` if not already done. SHA256-verified to make sure we're not backing up the Redirector itself after an unclean shutdown.
3. **Swap** — copy the bundled `redirector.exe` over `Tools\yt-dlp.exe`.
4. **Monitor** — `MonitorLoop` re-checks every 3 s. If VRChat updates and overwrites our redirector, the patch reapplies automatically.
5. **On graceful shutdown**, `RestoreYtDlpInTools()` swaps `yt-dlp-og.exe` back. `WipeToolsFolder()` and `CleanupJunk()` handle leftover plugin caches and stray logs.

`RestoreYtDlpInTools` is `public static` so the standalone Uninstaller can call it without booting the whole engine.

## Recovery from unclean shutdowns

If WKVRCProxy crashed with the patch still applied, the next launch detects:
- `yt-dlp.exe` exists and matches `redirector.exe` SHA256
- `yt-dlp-og.exe` exists

`RecoverFromUncleanShutdown` is idempotent: if both files are present and intact, the patch is just left in place (since we'd reapply it anyway). If `yt-dlp-og.exe` is somehow missing but `yt-dlp.exe` *is* a redirector, the engine flags this and prompts the user to reinstall VRChat's tools (we won't fabricate a yt-dlp).

## IPC

The Redirector is launched by VRChat as a child process. It needs to reach Core, which runs in a separate process (the UI). Two transports:

- **`HttpIpcServer`** (HTTP loopback) — primary; simpler to debug
- **`WebSocketIpcServer`** — used by the Redirector for streaming-like responses
- **`PipeServer`** — present but rarely used; legacy fallback

All three listen only on `127.0.0.1`. Port is allocated dynamically (range `22361–22370`) at startup and written to:

- `%LOCALAPPDATA%\WKVRCProxy\ipc_port.dat`
- `{vrcToolsDir}\ipc_port.dat`

The Redirector reads the second one (it's already running with VRChat's working dir, no AppData lookup needed). If both are missing, the Redirector falls back to invoking the real `yt-dlp-og.exe` if present, or returning the original URL pristine — VRChat's native resolver still gets a chance.

## ResolvePayload

JSON contract from Redirector to Core:

```json
{
  "Args": ["--get-url", "https://example.com/video"],
  "Env": { "...": "..." }
}
```

Args mirror what VRChat passed. Env is forwarded so cloud strategies and yt-dlp plugins can see whatever VRChat wanted them to see. The response is the resolved URL, base64-encoded, with a small JSON envelope for error reporting.

Chunked transfer is implemented because some env-var blocks (Steam controller configs especially) blow past the default WebSocket buffer.

## Why raw stdout?

VRChat's async reader for yt-dlp output is line-buffered and picky about encoding. The Redirector writes the resolved URL via:

```csharp
byte[] bytes = Encoding.UTF8.GetBytes(result.Trim() + "\n");
using var stdout = Console.OpenStandardOutput();
stdout.Write(bytes, 0, bytes.Length);
stdout.Flush();
```

Not `Console.WriteLine` (platform-specific newline), not `Console.Out.WriteAsync` (BOM hazard). Always exactly one trailing `\n`, no CRLF. See [[Engineering Standards]] for the full rationale and related rules.

## Single-instance mutex

Both the UI and the Updater respect `Local\WKVRCProxy.UI.SingleInstance`. If you double-click WKVRCProxy.exe twice, the second instance brings the first to the foreground and exits. The Updater polls this mutex (30 s timeout) before starting its swap, so the UI can't be running mid-swap.
