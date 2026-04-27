# Embed Website — Baseline

**Last reviewed: 2026-04-27** (against `main` at `1da7139`).

This is the snapshot the design in [DESIGN.md](DESIGN.md) is built from. It doesn't argue for an approach; it just records what is true today.

---

## 1. Program shell (WKVRCProxy.UI)

### Window & WebView

- Photino: `Photino.NET 4.0.16` ([WKVRCProxy.UI.csproj:14](../../src/WKVRCProxy.UI/WKVRCProxy.UI.csproj)).
- Window built in [Program.cs:314-321](../../src/WKVRCProxy.UI/Program.cs): 1200×800, single `PhotinoWindow`, `RegisterWebMessageReceivedHandler` for IPC.
- Content load is `_window.Load(indexPath)` ([Program.cs:395](../../src/WKVRCProxy.UI/Program.cs)) where `indexPath` is `wwwroot/index.html` next to the exe (prod) or `../../../src/WKVRCProxy.UI/ui/dist/index.html` (dev `dotnet run`). **Both load over `file://`.**
- WebView2 user-data dir is forced to `{baseDir}/WebView2_Data` via the `WEBVIEW2_USER_DATA_FOLDER` env var ([Program.cs:310-312](../../src/WKVRCProxy.UI/Program.cs)). Fresh, scoped to this app.
- Photino 4.0.16 does **not** expose the underlying `CoreWebView2` to managed code. The only host-side surface for the WebView is:
  - `_window.SendWebMessage(string)` — push to JS.
  - `RegisterWebMessageReceivedHandler` — receive from JS.
  - `Load(path)` / `LoadRawString(html)` — initial nav.
  - `RegisterCustomSchemeHandler(scheme, handler)` — Photino does support custom-scheme handlers (we don't currently use any).
- Consequence: we cannot call `AddScriptToExecuteOnDocumentCreatedAsync`, register a `WebResourceRequested` interceptor, or set CSP headers from C# without either upgrading Photino or adopting a custom scheme.

### IPC bridge (JS ↔ C#)

JS side ([main.ts:24-45](../../src/WKVRCProxy.UI/ui/src/main.ts), [appStore.ts](../../src/WKVRCProxy.UI/ui/src/stores/appStore.ts) `sendMessage`):

```js
window.photino = {
  sendMessage: msg => external.sendMessage(msg),
  receiveMessage: handler => external.receiveMessage(handler),
}
// envelope:
window.photino.sendMessage(JSON.stringify({ type: 'GET_CONFIG' }))
```

C# side dispatches in [Program.IpcHandlers.cs:167](../../src/WKVRCProxy.UI/Program.IpcHandlers.cs) via a single `switch (type)`.

Outbound (JS→C#) command surface today:

| Command | Purpose |
|---|---|
| `EXIT` | Close window |
| `GET_CONFIG` / `SAVE_CONFIG` | Settings round-trip |
| `OPEN_BROWSER` `{url}` | Shell-open in default browser |
| `SYNC_LOGS` | Replay log history |
| `GET_CHANGELOG` | Fetch embedded `CHANGELOG.md` |
| `PICK_VRC_PATH` | Folder picker (CommonOpenFileDialog) |
| `HOSTS_SETUP_ACCEPTED` / `HOSTS_SETUP_DECLINED` / `REQUEST_HOSTS_SETUP` | Hosts-file UAC flow |
| `ADD_FIREWALL_RULE` | netsh advfirewall add rule (UAC) |
| `START_P2P_SHARE` `{url}` / `STOP_P2P_SHARE` | Drives `WhyKnotShareService` |
| `REQUEST_CLOUD_RESOLVE` `{url}` | Cloud Link cascade resolution |
| `GET_HEALTH` | System health snapshot |
| `GET_BYPASS_MEMORY` / `FORGET_BYPASS_KEY` | Strategy memory ledger |
| `GET_YTDLP_UPDATE` / `GET_APP_UPDATE` / `APP_UPDATE_CHECK` | Updater status |
| `LAUNCH_UPDATER[_FORCE]` / `LAUNCH_UNINSTALLER[_FORCE]` | Sidecar launches |
| `SET_ANONYMOUS_REPORTING` `{optIn}` | Reporting consent |

Inbound (C#→JS) types: `LOG`, `CONFIG`, `STATUS`, `HEALTH`, `PROMPT`, `P2P_SHARE_STARTED/STOPPED/ERROR`, `CLOUD_RESOLVE_RESULT`, `RELAY_EVENT`, `BYPASS_MEMORY`, `YTDLP_UPDATE`, `APP_UPDATE`, `CHANGELOG`, `STRATEGY_DEMOTED`, `SIDECAR_ERROR`.

### Vue UI shape

- No `vue-router`. App.vue switches views via `appStore.activeTab` ([App.vue:312-320](../../src/WKVRCProxy.UI/ui/src/App.vue)).
- Sidebar tab list is hardcoded in [Sidebar.vue:7-15](../../src/WKVRCProxy.UI/ui/src/components/layout/Sidebar.vue): `dashboard, history, bypass, share, relay, logs, settings`.
- Views (one-line summary each, derived from reading them, not inferring):
  - **DashboardView** — live stats, tier distribution donut, sparkline, top 5 recent history, session uptime.
  - **HistoryView** — full resolution log, search + tier filter, height histogram.
  - **BypassView** — per-host strategy W/L ledger, yt-dlp update controls.
  - **ShareView** — two modes:
    - *Cloud Link* — calls `appStore.requestCloudResolve(url)` → `REQUEST_CLOUD_RESOLVE` → returns a direct CDN URL.
    - *P2P Stream* — calls `appStore.startP2PShare(url)` → `START_P2P_SHARE` → drives `WhyKnotShareService` (WebSocket to `wss://whyknot.dev/mesh`) and surfaces a public URL.
  - **RelayView** — per-chunk relay event stream + totals.
  - **LogsView** — live log + level/source filters.
  - **SettingsView** — config UI (preferred resolution, tiers, WARP, anonymous reporting, native-UA hosts, hosts-file/firewall, uninstall).

### `WhyKnotShareService` (the bridge ShareView ultimately drives)

Lives in `src/WKVRCProxy.Core/Services/WhyKnotShareService.cs`. Owns a `ClientWebSocket` to `wss://whyknot.dev/mesh`, sends `relay_init`, then services `relay_read` byte-range pulls by streaming from the resolved origin. **The website already does the same dance from its frontend** — that's the duplication this work targets.

### `AppConfig`

Defined in [AppConfig.cs:34-206](../../src/WKVRCProxy.Core/Models/AppConfig.cs); persisted to `app_config.json` next to the exe by `SettingsManager`. Mirror type in [appStore.types.ts:48-91](../../src/WKVRCProxy.UI/ui/src/stores/appStore.types.ts). Adding a new field requires:

1. Property + `[JsonPropertyName]` on `AppConfig` (C#).
2. Field on the `AppConfig` interface in `appStore.types.ts` (TS).
3. Default in `appStore.ts` `config` ref initializer.
4. Echo in `SAVE_CONFIG` handler in [Program.IpcHandlers.cs:188-233](../../src/WKVRCProxy.UI/Program.IpcHandlers.cs) so server-side state matches what the UI just sent.
5. *Optional:* registration in `DefaultTrackedFields.Resetters` if the default should track source on non-customized installs.

---

## 2. WhyKnot.dev (read-only baseline)

Read against `D:\Github\WhyKnot.dev` on 2026-04-27. We do not modify this repo here; cooperative changes go through a sibling chat — see [HANDOFF.md](HANDOFF.md).

### Framing posture (the headline question)

- `nginx.conf` adds `Cache-Control` only. No `X-Frame-Options`, no `Content-Security-Policy`, no `frame-ancestors`.
- `WhyKnot.Backend/Program.cs` configures CORS for `whyknot.dev` and subdomains and sets up SignalR / static-file serving. **No security-headers middleware.** No CSP middleware. No `X-Frame-Options` middleware.
- `WhyKnot.Frontend/index.html` has no `<meta http-equiv="Content-Security-Policy">`.
- Repo-wide search for `frame-ancestors`, `X-Frame-Options`, `XFrameOptions` → zero hits in non-archived sources.

**Verdict:** As of today, `https://whyknot.dev` ships **no framing-blocking headers**. The user's prior iframe attempt may have hit a browser policy (mixed-content, third-party cookies, storage partitioning) rather than an explicit framing block — verifying this is the first PoC step.

> ⚠ **Earlier exploration suggested cross-origin would prevent the iframe's own WebSocket to `wss://whyknot.dev/mesh` from connecting.** That's wrong. Inside the iframe the document's origin **is** `https://whyknot.dev`; a `new WebSocket('wss://whyknot.dev/mesh')` from that document is same-origin from the WS server's POV (handshake `Origin: https://whyknot.dev`). The parent frame being `file://` does not appear in the WS handshake. The iframe's mesh client should "just work."

### Routes worth embedding

`WhyKnot.Frontend/src/main.ts:15-27` (router):

| Route | Component | Notes |
|---|---|---|
| `/` | `Home.vue` | Resolver UI. Talks to `/mesh` WebSocket. |
| `/relay` | `Relay.vue` | P2P relay (file → public URL via mesh). Drag-and-drop file picker. |
| `/restream` | `Restream.vue` | RTMP restream to VRCDN ingest. Persists stream key in cookie (`vrcdn_stream_key`, `SameSite=Strict`). |
| `/archive` | `Archive.vue` | Internet-Archive browsing/streaming. |
| `/popcorn` | `Popcorn.vue` | Auth-gated metadata search (`popcorn_access` cookie, `SameSite=Strict`). |
| `/history` | `History.vue` | localStorage-only client history. |
| `/p2p` | (redirect) | → `/relay` (legacy). |

No existing `/embed` or `/program` route. No frame-context detection. No `postMessage` listeners (`grep` of `WhyKnot.Frontend/src` for `postMessage`/`window.parent`/`window.opener`/`wkBridge` returned 0 hits).

### Cookies / storage when iframed inside `file://`

Modern Chromium (which WebView2 is) implements **storage partitioning**: cookies, localStorage, IndexedDB created by an iframe are keyed to `(top-level origin, iframe origin)`. For our case the top is `file://` (always the same one — the program), and the iframe is `https://whyknot.dev`.

Implications:

- **Cookies set inside the iframe persist** across program launches (top-level is stable) but are **isolated** from cookies set by visiting whyknot.dev in a normal browser. The user's logged-in browser session does not carry over into the program.
- `popcorn_access` and `vrcdn_stream_key` will work — they just need to be set once *inside* the program. The browser session stays inside the program.
- Third-party-cookie blocking (Chrome's "Tracking Protection" / future 3PCD) may further restrict iframe cookies; WebView2 inherits the host Chromium policy. We pin this as a known-risk to verify in the PoC.

### `wss://whyknot.dev/mesh` protocol

Server-side: `WhyKnot.Backend/Program.cs:181-205` (`app.Map("/mesh", …)`). JSON action envelope:

```json
{ "action": "ping" | "resolve" | "relay_init" | "relay_ready" | "relay_error" | "relay_read" |
              "restream_start" | "restream_stop" | "restream_queue_add" | "restream_skip", … }
```

No client-identifying handshake. No "are you the desktop app?" capability negotiation. The frontend's `mesh.ts` store dials `wss://${window.location.host}/mesh` — so an iframe at `whyknot.dev` builds `wss://whyknot.dev/mesh` automatically.

### Static asset CSP from the page itself

Single off-origin reference in `WhyKnot.Frontend/index.html`: a Bootstrap-Icons stylesheet from `cdn.jsdelivr.net`. No `<meta http-equiv="Content-Security-Policy">`. No script-src restrictions. Assets are loaded with `crossorigin` for SRI but that does not affect framing.

---

## 3. What "consolidate" means in concrete terms

| Surface today | Native (program) does | Website does | Duplication? |
|---|---|---|---|
| Cloud Link resolve | Calls `_resEngine.ResolveForShareAsync` directly | `/` page calls `/mesh resolve` server-side | **Yes** — same idea, different code paths. Native uses local cascade; website goes via server. |
| P2P share | `WhyKnotShareService` ⇄ `wss://whyknot.dev/mesh` `relay_init` | `/relay` page does the same dance from JS | **Yes** — protocol is identical, two implementations. |
| RTMP restream | (not present) | `/restream` page | Not duplicated, but program could grow it. |
| History (per-user) | App stores in `app_config.json` | Stores in localStorage on whyknot.dev | Different scopes; intentional. |
| Settings, logs, updater, hosts-file, relay traffic, bypass memory, dashboard | Native | — | Native-only. **Stays native.** |

The ShareView duplication is the strongest motivation. Consolidating it eliminates ≈one whole view, two IPC commands (and their three inbound replies), and the `WhyKnotShareService` (whose only consumer is ShareView).

---

## 4. Constraints inherited from the brief

- **Don't delete existing Share view yet.** PoC ships in addition to it, default-off.
- **Don't modify whyknot.dev.** Read-only. Cooperative server changes are sibling-chat work.
- **Don't touch `tools/vrchat-net-capture/`.** Sibling chat owns it.
- **Native bridge security:** if a bridge ever exists between the embedded site and the program, it has a strict allowlist of message types — see [DESIGN.md § Bridge protocol](DESIGN.md#bridge-protocol).
