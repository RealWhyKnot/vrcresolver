# Embed Website — Design

This document picks an approach for surfacing whyknot.dev features inside the WKVRCProxy desktop program without duplicating each feature in C# + Vue. Read [BASELINE.md](BASELINE.md) first.

**Status:** approach selected, **PoC merged dark behind `enableWebsiteTab` (default `false`)**. Full implementation, native bridge, and migration of existing views are deferred — see [HANDOFF.md](HANDOFF.md).

**Scope:** consolidate user-facing surfaces (Share, Restream, Archive, Popcorn, History) where the website is already the canonical implementation. Keep native-only surfaces native (Settings, Logs, Updater, Bypass, Relay Traffic, Dashboard, Hosts-file/Firewall flow) — those need backend data the website cannot have.

---

## Approaches considered

Five reasonable paths. Scored on: maintenance burden after migration, regression risk during migration, native-bridge story, dev/prod parity, perf, cross-repo coordination required.

### A — Same-window iframe to whyknot.dev

A `<WebsiteView>` Vue component renders `<iframe src="https://whyknot.dev">`. Parent ↔ iframe talk via `window.postMessage`; the iframe ships an SDK that detects "running inside the program" and proxies file-picker / clipboard / open-in-browser calls back through the program's existing IPC bridge.

| Axis | Verdict |
|---|---|
| Maintenance | **Low.** Website team owns the UX; program just embeds. |
| Regression risk | **Low.** Existing native views stay; embed is additive. |
| Native bridge | **Possible** via `postMessage` with an origin allowlist. Requires website to ship an SDK shim (small). |
| Dev/prod parity | Easy: prod points at `https://whyknot.dev`, dev points at `http://localhost:5173` (whyknot frontend dev server) via the same flag. |
| Perf | One extra Chromium frame. WebView2 isolates by site, so memory cost is real (≈30-80 MB) but acceptable. |
| Cross-repo | **Maybe none.** BASELINE.md confirms whyknot.dev currently emits no `X-Frame-Options` / CSP frame-ancestors header — should frame today. The cooperative ask (a real `frame-ancestors` allowlist that *explicitly* permits the program origin) is a defensive add, not strictly required. |

### B — Multiple WebView2 instances in one Photino window

Use Photino to host two WebViews — one for the program shell, one for the website — side-by-side or stacked.

| Axis | Verdict |
|---|---|
| Maintenance | Lower than custom shimming, but Photino does not expose multi-WebView in 4.0.16. We'd need to either drop Photino for `Microsoft.Web.WebView2.Wpf` or wait for Photino to expose it. |
| Regression risk | **High.** Replacing the windowing layer touches every UI flow we already ship. |
| Native bridge | Each WebView gets its own message channel — clean separation, but needs a host-side router. |
| Dev/prod parity | Same as A. |
| Perf | Two browser processes. |
| Cross-repo | Same framing question as A. |

**Disqualified:** windowing rewrite is too disruptive for the value gained over A.

### C — Single WebView, navigate the whole window to whyknot.dev with bridge injection

Replace the file:// shell with a navigation to `https://whyknot.dev/program`. C# uses `AddScriptToExecuteOnDocumentCreatedAsync` to inject `window.wkBridge.*` before page load. The website grows a `/program` route that hosts native-equivalent views (settings, logs, history, etc.) and detects the bridge.

| Axis | Verdict |
|---|---|
| Maintenance | **Medium-high long-term.** Website owns most UI but must mirror desktop-only flows (settings, logs, updater, bypass memory) — we'd be moving the duplication, not removing it. |
| Regression risk | **High.** Total rewrite of the program UI delivery path. |
| Native bridge | Photino 4.0.16 **does not expose CoreWebView2**. We'd need a Photino upgrade or a custom-scheme-handler hack. Not a one-day spike. |
| Dev/prod parity | Hard — dev means running whyknot.dev's frontend locally on port 5173, hosting `/program` there, and getting the bridge injection to fire on the dev origin. |
| Perf | Same as today. |
| Cross-repo | **High.** whyknot.dev grows a `/program` shell, native-equivalent views, version skew handling. |

**Disqualified for now:** the cross-repo coupling and Photino-CoreWebView2 limitation are blockers; the migration cost dwarfs the duplication we're trying to remove.

### D — Shared Vue component package (`@whyknot/ui-shared`)

Extract the user-facing shared views into a workspace package consumed by both `WKVRCProxy.UI/ui` and `WhyKnot.Frontend`. Each app composes the shared components with its local capabilities (file picker, IPC, etc.).

| Axis | Verdict |
|---|---|
| Maintenance | **Lowest after migration.** Single source of truth for the shared UX. |
| Regression risk | **High during the migration itself** — every shared component needs an injection seam, and both consumers must rev together. |
| Native bridge | Each consumer wires the bridge as a Vue provide/inject. Cleanest of any option. |
| Dev/prod parity | Easy — npm workspace. |
| Perf | Zero overhead vs today. |
| Cross-repo | **Highest one-time cost.** Two repos, one package, version-pinning, release coordination. |

**Right answer for the long term, wrong answer for "the user is tired today."** Holding for Phase 3.

### E — Hybrid: program shell + per-feature embed

Keep the program shell native; add an embed for *individual* feature pages (`https://whyknot.dev/embed/share`, `…/embed/restream`). Website ships a thin `embed/*` layout and a `wkBridge` SDK; program embeds whichever feature it's routing to.

| Axis | Verdict |
|---|---|
| Maintenance | **Medium.** Website owns each feature; program owns the shell. Each embedded route is a contract the website must keep stable. |
| Regression risk | **Low** if rolled out feature-by-feature behind flags. |
| Native bridge | Same as A; per-feature pages can declare their bridge needs in the SDK. |
| Dev/prod parity | Same as A. |
| Perf | Same as A. |
| Cross-repo | **Medium.** `embed/*` routes + bridge SDK on whyknot.dev. |

**Compelling but premature** — we don't yet know whether one full-website embed (A) is good enough on its own. If it is, E is unnecessary structure.

---

## Recommendation

**Approach A (full-website iframe), shipped in two phases.**

### Phase 1 — passive embed (this PoC)

Add a `WebsiteView` that renders `<iframe src="https://whyknot.dev">` with a thin top bar. No bridge. The user-facing experience is "the website, inside the program window, with browser back-buttons." Validates:

- The iframe actually loads in WebView2 from a `file://` parent. (Critical — this is what the user has hit before.)
- The iframe's own WebSocket to `wss://whyknot.dev/mesh` works (it should; same-origin from the iframe's POV — see BASELINE.md "Framing posture").
- Cookies / localStorage persist across program launches under WebView2's storage partitioning.
- File pickers (`<input type="file">`) inside the iframe behave well in WebView2 — they should open the standard Windows file dialog.

Nothing else changes. ShareView keeps shipping. The new tab is **off by default** behind `enableWebsiteTab`.

### Phase 2 — narrow native bridge

If Phase 1 validates, add a `wkBridge` SDK on whyknot.dev that the iframe uses to call back into the program for things the website alone can't do. Strict allowlist:

#### Bridge protocol

The iframe (origin: `https://whyknot.dev`) calls into the program via `window.parent.postMessage`. The program window listens on `window` (in `WebsiteView.vue`) and forwards approved messages to Photino's IPC.

**As shipped, the protocol is the one implemented in [`WhyKnot.Frontend/src/lib/wkBridge.ts`](https://github.com/RealWhyKnot/WhyKnot.dev/blob/main/src/WhyKnot.Frontend/src/lib/wkBridge.ts) and [`src/WKVRCProxy.UI/ui/src/views/WebsiteView.vue`](../../src/WKVRCProxy.UI/ui/src/views/WebsiteView.vue) — anything below that conflicts with those files is wrong; the canonical shape is defined by the code.**

**1. Handshake (iframe → program → iframe)**

On load, the SDK posts:

```ts
window.parent.postMessage(
  { type: 'wkBridgeHello', version: 1, origin: window.location.origin },
  '*', // unavoidable until the iframe knows its parent's origin
);
```

The program's `WebsiteView.vue` validates `event.origin` against the embed allowlist (prod: `https://whyknot.dev`; dev: `http://localhost:5173` only when `import.meta.env.DEV`), then replies via `event.source.postMessage(..., event.origin)` with:

```ts
{
  type: 'wkBridgeReady',
  version: 1,
  features: ['PICK_FILE', 'COPY_TO_CLIPBOARD', 'OPEN_IN_BROWSER',
             'GET_HISTORY', 'GET_VERSION', 'GET_LAST_LINK'],
  programOrigin: 'null', // Photino loads index.html from file:// → opaque origin "null"
}
```

The SDK refuses to flip into "embedded" mode until it receives a `wkBridgeReady` whose claimed `programOrigin` matches the actual `event.origin` (or is `'null'`, the documented escape clause for opaque file:// parents). After the handshake, `programOrigin` is the strict origin check applied to every subsequent `wkBridgeResponse`.

**2. Method call (iframe → program)**

```ts
window.parent.postMessage(
  { type: 'wkBridge', method, requestId: '<uuid>', args: { … } },
  programOrigin, // explicit, never '*'
);
```

**3. Response (program → iframe)**

The program forwards the approved call to Photino IPC as `WEBSITE_BRIDGE`; the C# side dispatches against the same allowlist (second line of defense — origin validation in `WebsiteView.vue` is the first) and replies via `WEBSITE_BRIDGE_RESPONSE`. `WebsiteView.vue` routes that back to the originating iframe's `contentWindow` with the iframe's origin as the explicit `targetOrigin` (never `'*'`):

```ts
event.source.postMessage(
  { type: 'wkBridgeResponse', requestId, ok: boolean, data?: …, error?: string },
  event.origin,
);
```

**Allowlist (host-enforced; anything else is dropped + logged once per session per (origin, method) tuple):**

| `method` | `args` | `data` on success |
|---|---|---|
| `PICK_FILE` | `{ accept?: string[] }` | `{ path, name, sizeBytes }` (or `ok: false, error: 'cancelled'`) |
| `COPY_TO_CLIPBOARD` | `{ text }` | (none — `ok: true`) |
| `OPEN_IN_BROWSER` | `{ url }` | (none — `ok: true`; rejects non-http(s) schemes) |
| `GET_HISTORY` | none | `{ history: [{ link, resolvedAt, type? }] }` |
| `GET_VERSION` | none | `{ version }` |
| `GET_LAST_LINK` | none | `{ link, resolvedAt }` or `null` |

`PICK_FILE` returns *path only* — uploading is the website's job over its own HTTPS. `COPY_TO_CLIPBOARD` exists mostly for parity in case `navigator.clipboard.writeText` is blocked in the cross-origin iframe.

**Origin handling:** the program holds `WebsiteEmbedOrigin = "https://whyknot.dev"` (config-overridable for dev where it becomes `http://localhost:5173`). Any `postMessage` whose `event.origin` doesn't match is dropped *before* type-routing. This is the security boundary.

**Why the allowlist is small on purpose:** a future malicious iframe (compromise of whyknot.dev, MITM in the future, or a developer mis-pointing the origin) gets `PICK_FILE` (user-gated UI), clipboard write, browser open, read-only history. No way to reach the relay, the patcher, the updater, hosts-file edit, or settings save. The blast radius caps at "annoy the user with file dialogs."

### Native flows that stay native (do not move to embed)

| Surface | Why it stays |
|---|---|
| Settings | Reads/writes `app_config.json` directly; no website equivalent. |
| Logs | Streamed from native code via IPC `LOG`. |
| Bypass memory | Uses `strategy_memory.json`. |
| Relay Traffic | Per-chunk events from `RelayServer` only the program sees. |
| Dashboard | Aggregates the above + session uptime. |
| Updater / Uninstaller / hosts-file / firewall | UAC, sidecar processes, OS access. |
| Hosts-setup prompt + sidecar error modal | Native UX gates triggered by lifecycle events. |

These belong in the program because they reach into the OS or operate on data the website never sees. We are not moving them.

### Surfaces that go away over time (deferred — not part of this PoC)

| Today | After embed proves itself |
|---|---|
| `ShareView.vue` (Cloud Link + P2P) | Removed — the website's `/` and `/relay` cover both. |
| `WhyKnotShareService.cs` | Removed — its only consumer is ShareView. |
| `START_P2P_SHARE` / `STOP_P2P_SHARE` IPC | Removed. |
| `REQUEST_CLOUD_RESOLVE` IPC | Reused by the embed via `wkBridge` only if a "resolve from local cascade" hook is exposed. Otherwise, removed too — the website's mesh resolver is the canonical path for shared URLs. |

The deletion happens in a follow-up PR after the PoC has been used in real conditions for a release cycle.

---

## Migration plan

| Step | When | What |
|---|---|---|
| 0 | Now (this PR) | PoC behind `enableWebsiteTab=false`. Tab not surfaced unless user flips the flag in `app_config.json`. |
| 1 | Sibling whyknot.dev chat | Defensive `Content-Security-Policy: frame-ancestors 'self' file: https://*` on whyknot.dev. (Currently no XFO/CSP exists, so the iframe will load — but pinning the policy explicitly prevents drift.) Optional: `Permissions-Policy: clipboard-write=(self "https://*"), fullscreen=(self "https://*")`. |
| 2 | This repo | Surface a Settings → Advanced toggle for `enableWebsiteTab` so testers can flip without editing JSON. |
| 3 | This repo | Phase 2 bridge: implement the `wkBridge` listener on the program side, plumb the `PICK_FILE` IPC, document the allowed message types. |
| 4 | Sibling whyknot.dev chat | Ship the `wkBridge` SDK on whyknot.dev. Add the iframe-aware affordances (e.g. file picker on `/relay` calls `wkBridge.pickFile()` when running embedded; otherwise falls back to the existing browser file input). |
| 5 | This repo | Default `enableWebsiteTab=true` for one release as a soft-launch. |
| 6 | This repo | Delete `ShareView.vue`, `WhyKnotShareService`, `START_P2P_SHARE`/`STOP_P2P_SHARE`/`REQUEST_CLOUD_RESOLVE` IPC. |

Each step is reversible. Steps 0 and 1 are independently mergeable.

---

## Server-side asks for whyknot.dev (sibling chat)

Listed here so the user can paste the brief into a fresh whyknot.dev session. Detail in [HANDOFF.md](HANDOFF.md).

1. **Pin a `Content-Security-Policy: frame-ancestors` policy** that explicitly allows the program origin (`file:` for the bundled build; the dev origin if/when we move to a custom scheme). Currently no header is set, so the iframe works today by default — pinning the policy prevents an unrelated future change from accidentally breaking embedding.
2. **Optional:** `Permissions-Policy` allowlists for `clipboard-read`, `clipboard-write`, `fullscreen`, and `display-capture` so embedded use of those APIs doesn't fail silently.
3. **Phase 2 only:** implement the `wkBridge` SDK in the frontend. Spec is in this doc; the iframe detects `window.self !== window.top` AND that `document.referrer.startsWith('file:')` (or the configured embed parent) and switches affordances accordingly (e.g. file picker uses native dialog via bridge instead of `<input type=file>`).

No nginx changes are strictly required today (no XFO/CSP currently set). The asks are defensive.

---

## Dev-mode story

Today the program loads its UI from `file://wwwroot/index.html` and that's true in dev too (`dotnet run` falls back to `src/WKVRCProxy.UI/ui/dist/index.html`). The website tab pulls in a *separate* origin, so its dev source is independent.

Two knobs supported by the design:

- `WebsiteEmbedUrl` (default `https://whyknot.dev`) — config field in `AppConfig`. Set this to `http://localhost:5173` while iterating against a local whyknot.dev frontend.
- `WebsiteEmbedOrigin` (derived) — used for `postMessage` origin validation in Phase 2. Computed from the URL, not stored separately.

Phase 1 PoC hardcodes `https://whyknot.dev` to keep the surface minimal. Phase 2 adds the config field.

---

## Risks & how the PoC clears them

| Risk | Mitigation in PoC |
|---|---|
| WebView2 won't iframe a remote HTTPS origin from a `file://` parent (sandbox rules). | PoC tests this directly. If it fails, document the exact error, and Phase 2 escalates to a custom scheme handler. |
| `wss://whyknot.dev/mesh` from inside the iframe won't connect. | Same — verify in the PoC. Expected to work since the iframe's document origin is `https://whyknot.dev`. |
| Cookie/storage partitioning makes auth-gated pages (Popcorn) re-prompt every session. | Storage partitioning is by `(top, embedded)` and the top is stable (always the program), so cookies persist *across program launches* — just isolated from the user's normal browser. PoC verifies. |
| Third-party-cookie blocking (Chrome 134+) breaks the iframe's first-party WS handshake. | The WS is same-origin from the iframe; doesn't fall under 3PCD. PoC verifies. |
| User is offline and the iframe shows a generic Chromium error. | Out of scope for PoC; Phase 2 adds a "Website unavailable" overlay with a "retry" button. |
| Whyknot.dev makes a breaking change to its UI mid-session. | Out of scope; the website is treated as a black box. Major UI changes get a header on whyknot.dev's side. |

---

## What this PoC does *not* do

- Does not implement `wkBridge`. Phase 2.
- Does not retire ShareView, WhyKnotShareService, or any IPC commands. Phase 6.
- Does not surface a Settings toggle for `enableWebsiteTab`. Manual JSON edit only — keeps the surface dark.
- Does not point at a dev URL. Hardcoded `https://whyknot.dev` to keep the patch small.
- Does not add CSP / `frame-ancestors` on whyknot.dev. Sibling chat.
- Does not measure perf / memory. Manual sanity check post-merge.
