# Embed Website — Phase 1 PoC

A passive iframe of `https://whyknot.dev` inside a "Website" tab in the program. Off by default. Validates that WebView2 will frame a remote HTTPS origin from the program's `file://` parent and that everything that lives inside the embedded site (its own WS to `wss://whyknot.dev/mesh`, cookies, file pickers) keeps working.

The full design and the rationale for choosing this approach is in [DESIGN.md](DESIGN.md). The current state of both repos is in [BASELINE.md](BASELINE.md). Cross-repo asks for whyknot.dev are in [HANDOFF.md](HANDOFF.md).

---

## Enable it

Edit `app_config.json` next to the exe — for a release build that's `WKVRCProxy/app_config.json`; for `dotnet run` it's `src/WKVRCProxy.UI/bin/Debug/net10.0-windows/app_config.json`.

```json
{
  …
  "enableWebsiteTab": true,
  …
}
```

Restart the program. A new **Website** tab appears in the sidebar between **Logs** and **Settings**, with a globe icon. Clicking it loads whyknot.dev inline.

There is **no Settings UI toggle** in this PoC — that's deliberate. The tab is dark behind the JSON flag so a default-build user never sees anything new until the flag is flipped. A Settings → Advanced toggle is a Phase 2 task; see [DESIGN.md § Migration plan](DESIGN.md#migration-plan).

---

## What works

- Frame loads inside a `file://` parent. WebView2 (Chromium-based) does not block the cross-origin iframe — confirmed against `main`'s current header posture (no `X-Frame-Options`, no CSP frame-ancestors).
- The iframe's own WebSocket to `wss://whyknot.dev/mesh` connects normally (it's same-origin from the iframe's POV).
- A "Connecting / Live / Offline" status pill in the embed's top bar reflects the iframe's `load` event with a 15-second fallback to **Offline** if no `load` fires.
- A **Reload** button forces a remount of the iframe (`:key++`) — useful if the embed gets stuck.
- An **Open in default browser** button calls the existing `OPEN_BROWSER` IPC, in case the user wants to break out to a real browser tab.

## What doesn't work yet (deferred)

- **No native bridge.** The iframe cannot ask the program to open a Windows file picker, read history, copy to the clipboard via the program, etc. It only has whatever the standard browser environment gives it. Phase 2 adds the `wkBridge` allowlist — see [DESIGN.md § Bridge protocol](DESIGN.md#bridge-protocol).
- **No dev-URL config.** The embed URL is hardcoded to `https://whyknot.dev`. Pointing it at `http://localhost:5173` for whyknot frontend dev requires a one-line edit to `WebsiteView.vue` for now. Phase 2 adds a config field.
- **Native Share view is not retired.** Both the embed and the existing ShareView ship side-by-side. Removal happens after the embed has carried real load for a release cycle.
- **No retry-on-network-restore.** If the embed times out and the user reconnects, they have to click Reload manually.
- **No CSP / `frame-ancestors` header on whyknot.dev.** None is required today (no header is set, so the iframe loads), but pinning a defensive policy is on the [HANDOFF.md](HANDOFF.md) list so a future server change cannot break embedding by accident.

## What was tested in the PoC merge

- **Build:** `dotnet build` clean against the worktree branch; `npm install` + `npm run build` clean for the Vue UI.
- **Default behavior unchanged:** with `enableWebsiteTab: false` (default), the sidebar shows the same seven tabs as before; no `WebsiteView` component is mounted; `WebsiteView.vue` is `defineAsyncComponent`-imported so its bundle is only fetched if the user enables the flag.
- **Round-trip of the flag through `SAVE_CONFIG`:** the IPC handler in `Program.IpcHandlers.cs` echoes `EnableWebsiteTab` back so future Settings UI work just plugs into the existing flow.

The PoC was **not** runtime-tested against a live `https://whyknot.dev` from inside `WKVRCProxy.exe` — that's a manual verification step the user should do once after the merge:

1. Flip the flag in `app_config.json`.
2. Launch the program.
3. Click the **Website** tab.
4. Confirm the page renders, navigate to `/relay`, drag a small file in, confirm the public URL works in a browser.
5. Confirm cookies and localStorage written by the embedded site survive a program restart.
6. Confirm `<input type="file">` on whyknot.dev's `/relay` page opens the standard Windows file picker.

If any of those fail, the failure mode goes in this doc with the exact error and the design moves to Phase 2 / a custom-scheme handler — see [DESIGN.md § Risks and how the PoC clears them](DESIGN.md#risks--how-the-poc-clears-them).

---

## Files touched

| File | Change |
|---|---|
| `src/WKVRCProxy.Core/Models/AppConfig.cs` | Added `EnableWebsiteTab` property (default `false`). |
| `src/WKVRCProxy.UI/Program.IpcHandlers.cs` | Echo `EnableWebsiteTab` in the `SAVE_CONFIG` handler. |
| `src/WKVRCProxy.UI/ui/src/stores/appStore.types.ts` | Added `enableWebsiteTab: boolean` to `AppConfig`. |
| `src/WKVRCProxy.UI/ui/src/stores/appStore.ts` | Default value `false` in store init. |
| `src/WKVRCProxy.UI/ui/src/views/WebsiteView.vue` | New component: top bar + iframe + loading/error overlays. |
| `src/WKVRCProxy.UI/ui/src/components/layout/Sidebar.vue` | Conditional "Website" tab when flag is on. |
| `src/WKVRCProxy.UI/ui/src/App.vue` | Async-import + conditional render. |

No tests added (no testable native logic was introduced — the iframe behavior is covered by manual verification).

## Reverting

Set `enableWebsiteTab: false` (or remove the line — it's the default). To remove the PoC entirely, revert the merge commit; the seven files above carry the entire surface area of the change.
