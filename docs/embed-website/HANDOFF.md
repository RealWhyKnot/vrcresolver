# Embed Website — Handoff

This documents the follow-up work the user (RealWhyKnot) should kick off in fresh chats now that the PoC is merged dark on `main`. Each item is self-contained — paste the brief into a new chat, give it the relevant repo, and it has what it needs.

For context: read [BASELINE.md](BASELINE.md) and [DESIGN.md](DESIGN.md) first. The PoC's status is in [POC.md](POC.md).

---

## 1. Sibling chat: whyknot.dev — pin defensive framing headers

**Repo:** `D:\Github\WhyKnot.dev` (private companion).
**Branch:** `production` (per `project_whyknot_dev` memory).
**Estimated size:** small (one nginx + one Program.cs / middleware change).

### What to do

Today whyknot.dev sets no `X-Frame-Options`, no `Content-Security-Policy: frame-ancestors`, and no `<meta http-equiv="Content-Security-Policy">`. So the WKVRCProxy desktop program can iframe `https://whyknot.dev` from its `file://` parent without server cooperation — that's how the PoC works.

But "no policy at all" means a future change (a security middleware add, a CSP header bolt-on, a third-party module that injects XFO) can silently break the desktop embed. Pinning an explicit policy that *allows* the desktop program prevents that drift.

**Add to `nginx.conf`** (or to the backend's headers middleware — pick whichever is the canonical layer for security headers in that repo):

```nginx
add_header Content-Security-Policy "frame-ancestors 'self' file: https://*" always;
```

Rationale for each source:

- `'self'` — whyknot.dev framing itself (existing same-origin behavior).
- `file:` — the WKVRCProxy desktop program loads its UI from `file://`. WebView2 on Windows runs in this origin.
- `https://*` — keeps any future https-served program shells working without another header change. **Tighten** if a stricter policy is preferred — e.g. drop `https://*` and add explicit known-program origins as they appear.

### Optional (recommended) — Permissions-Policy

If the embedded `/relay` page wants to use `clipboard-write` or `fullscreen` from inside the iframe, those features need to be allowed by the parent. Since the program embeds whyknot.dev (not the other way around), the *frontend's* `Permissions-Policy` matters less than whether the program's `<iframe>` opts in. The current PoC iframe does not set `allow=`, which is fine for clipboard/popup but explicitly denies `fullscreen`. If we want fullscreen video in the embed later, add `allow="clipboard-read; clipboard-write; fullscreen"` to the iframe in `WebsiteView.vue` *and* land a `Permissions-Policy` allowlist on whyknot.dev.

### What this chat should produce

- One commit on `production` (or a PR depending on the chat's policy) modifying nginx.conf (and/or the backend headers middleware).
- A test confirming `curl -sI https://whyknot.dev/ | grep -i frame` shows the new CSP header.
- A note in the whyknot.dev changelog that the policy was pinned and *why*.

### What this chat must NOT do

- Do not break framing. After the change, the WKVRCProxy desktop program (with `enableWebsiteTab: true`) must still load `https://whyknot.dev` in its iframe.
- Do not add `frame-ancestors 'none'` or `X-Frame-Options: DENY`.
- Do not deploy the change to production without the user verifying — pin to a staging environment first if available.

---

## 2. Sibling chat: whyknot.dev — `wkBridge` SDK (Phase 2)

**Repo:** `D:\Github\WhyKnot.dev`.
**Estimated size:** medium (frontend SDK + per-feature integrations).
**Pre-req:** WKVRCProxy ships the program-side bridge listener first (item 3 below).

### What to do

The desktop program embeds whyknot.dev. Phase 1 is a passive embed. Phase 2 lets the embedded site call back into the program for native capabilities the browser doesn't have — primarily a Windows file picker for the `/relay` upload flow.

The full protocol is in [DESIGN.md § Bridge protocol](DESIGN.md#bridge-protocol). Summary:

- Iframe `postMessage`s the parent (program) with `{ source: 'wkBridge', version: 1, id, type, payload }`.
- Program forwards approved `type`s to its existing IPC bridge and `postMessage`s a response back.
- **Allowed `type`s:** `PICK_FILE`, `COPY_TO_CLIPBOARD`, `OPEN_IN_BROWSER`, `GET_HISTORY`, `GET_VERSION`, `GET_LAST_LINK`. Anything else is dropped.

### Frontend work

1. Add `src/WhyKnot.Frontend/src/lib/wkBridge.ts`:
   - Detect embedded context: `window.self !== window.top` AND `document.referrer.startsWith('file:')` (loose check; tighten to a known parent-origin pattern in production).
   - Implement the request/response protocol with promise resolution keyed on `id`.
   - Export typed methods: `pickFile()`, `copyToClipboard(text)`, `openInBrowser(url)`, `getHistory()`, `getVersion()`, `getLastLink()`.
   - Each method falls back to a browser-native equivalent (`<input type="file">` for `pickFile`, `navigator.clipboard.writeText` for `copyToClipboard`, `window.open` for `openInBrowser`, no-op for the others) when not embedded.

2. Wire `pickFile` into `Relay.vue`'s drag-and-drop / file-input flow: when `wkBridge.isEmbedded()`, prefer the native picker.

3. Add a small "Embedded in WKVRCProxy" indicator somewhere subtle so users embedded in the program can tell they're not in their normal browser session.

### What this chat should produce

- The SDK module + types.
- A consumer in `Relay.vue` (and any other view that benefits — `Restream.vue`, the auth flows on `Popcorn.vue`).
- No regressions to the standalone web build — `wkBridge.isEmbedded()` falls false in normal browsers and every method falls back to the standard implementation.

---

## 3. Sibling chat: WKVRCProxy — Phase 2 native bridge implementation

**Repo:** this one (`D:\Github\WKVRCProxy`).
**Estimated size:** small-to-medium (one IPC command, one postMessage listener, one origin allowlist, settings toggle).
**Pre-req:** whyknot.dev's `wkBridge` SDK (item 2).

### What to do

1. **Settings → Advanced toggle** for `enableWebsiteTab`. One checkbox in `SettingsView.vue` plus the existing `markOverridden` plumbing. Drops the "edit JSON" requirement for testers.

2. **Config field for the embed URL** — add `WebsiteEmbedUrl: string` to `AppConfig.cs` (default `https://whyknot.dev`) and surface it as a hidden Advanced field. Edit `WebsiteView.vue` to use it instead of the hardcoded constant. Validates in dev against `http://localhost:5173`.

3. **postMessage listener** in `WebsiteView.vue` (or a small composable in `composables/useWkBridge.ts`):
   - Listen on `window` for `message` events.
   - Drop anything where `event.origin` doesn't match the configured embed origin.
   - Drop anything where `data.source !== 'wkBridge'` or `data.version !== 1`.
   - Switch on `data.type` against the allowlist (`PICK_FILE`, `COPY_TO_CLIPBOARD`, `OPEN_IN_BROWSER`, `GET_HISTORY`, `GET_VERSION`, `GET_LAST_LINK`). Unknown types: log once per (origin, type) per session; drop.
   - For `PICK_FILE`, send a new IPC `PICK_FILE` (data: `{ accept?: string[] }`) → C# opens `CommonOpenFileDialog` (single file) → returns `{ path, name, size }` or `null`. The website never receives bytes from the bridge — it reads bytes itself via the resolved path's local file URL or by uploading to its own backend.
   - For each request, post the response back to `event.source` (the iframe `Window`) with the matching `id`.

4. **Native IPC additions** in `Program.IpcHandlers.cs`:
   - `PICK_FILE` — `CommonOpenFileDialog` (no folder picker — `IsFolderPicker = false`); accepts the optional `accept[]` filter list.
   - The other bridge `type`s map to existing IPC (`OPEN_BROWSER`) or are pure read-throughs of `appStore` state (`GET_HISTORY`, `GET_VERSION`, `GET_LAST_LINK`) and don't need new IPC.

5. **Tests / verification**:
   - Manual: with the bridge wired, click "Pick file" inside the embedded `/relay` page and confirm a Windows file picker opens.
   - Manual: send a malformed `postMessage` from devtools and confirm it's dropped silently (and logged once).

### What this chat must NOT do

- Do not retire `ShareView.vue` or `WhyKnotShareService` yet. That's Phase 6 (after the embed has shipped to real users for one release cycle).
- Do not flip `enableWebsiteTab` default to `true`. That's Phase 5.
- Do not allow the bridge to dispatch IPC commands from the existing surface (e.g. `LAUNCH_UPDATER`, `START_P2P_SHARE`). The allowlist is the entire bridge surface.

---

## 4. Sibling chat: WKVRCProxy — retire ShareView + WhyKnotShareService (Phase 6)

**Repo:** this one.
**Estimated size:** small (deletions + IPC command removals).
**Pre-req:** Phases 2-5 have shipped and the embed has handled real share/relay traffic for at least one release cycle (a few weeks).

### What to do

After the embed has proven itself in production:

1. Delete `src/WKVRCProxy.UI/ui/src/views/ShareView.vue`.
2. Delete `src/WKVRCProxy.Core/Services/WhyKnotShareService.cs`.
3. Remove `START_P2P_SHARE`, `STOP_P2P_SHARE`, `REQUEST_CLOUD_RESOLVE` IPC handlers in `Program.IpcHandlers.cs`.
4. Remove the corresponding store state + actions in `appStore.ts` (`p2pShare*`, `cloudResolve*`).
5. Remove the inbound `P2P_SHARE_*` and `CLOUD_RESOLVE_RESULT` message handlers.
6. Remove the **Share** tab from `Sidebar.vue`.
7. Update CHANGELOG and the wiki's [Settings Reference](https://github.com/RealWhyKnot/WKVRCProxy/wiki/Settings-Reference) entry.

This is intentionally a separate chat — it's a *deletion PR* and shouldn't be coupled with the *introduction PR* (item 3) so it stays cleanly revertable if real-world embed use turns up something unexpected.

### What this chat must NOT do

- Do not delete the relay/proxy *core* — only the user-facing share flow goes away. The core relay continues to exist for VRChat URL bypass; only the `WhyKnotShareService` (which targets the share-with-a-friend flow specifically) is retired.
- Do not delete history. The embed's `/history` page is per-browser-session; the program's history is per-install. They serve different needs.

---

## Open questions

These came up during design but didn't have an obvious answer. Worth flagging in whichever chat picks up Phase 2:

1. **Should `wkBridge.openInBrowser` work on any URL or only on whitelisted hosts?** The existing `OPEN_BROWSER` IPC opens whatever URL is sent. From the embedded site the bridge inherits the same trust. A user-malicious whyknot.dev could spam `OPEN_BROWSER` to advertise tracking links. Mitigation: cap at one `OPEN_BROWSER` per second; show a brief "Opening in your browser..." toast so the user notices.
2. **Cookies / sessions split.** The user's normal-browser whyknot.dev session is fully isolated from the embedded program session (storage partitioning by top-level origin). If the user is logged in via cookie auth (none today, but planned), they'd need to log in again inside the program. Not blocking but worth a Settings note.
3. **What happens if the user sets `enableWebsiteTab: true` and `WebsiteEmbedUrl` to a non-whyknot.dev URL?** The bridge origin allowlist gates this. Any non-allowed origin is treated as untrusted — drops all bridge messages. No surprise IPC. (Document this in the Phase 2 chat brief explicitly.)
