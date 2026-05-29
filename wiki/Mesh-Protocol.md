# Mesh Protocol

The watchdog talks to proxy.whyknot.dev over a single persistent WebSocket. The wire shape has versioned forward/backward like an HTTP API does -- the watchdog can talk to any server back to v1 (legacy) and any server up to its own client version.

## What v3 added (current -- 2026-05-04)

**Subprotocol:** `whyknot-v3` literal in the WebSocket upgrade. If the server (or an intermediate proxy) doesn't echo it back, the client silently falls back to v2 behavior. The user-visible console doesn't change either way.

**Compression:** RFC 7692 permessage-deflate offered on the upgrade. Server can refuse -- the connection is uncompressed-but-functional in that case. When negotiated, every frame is compressed before it leaves the server's egress, so the welcome and resolve_log streams are roughly half their v2 size on the wire.

**Welcome cache:** The server's welcome frame is mostly unchanging across reconnects (engine list, feature list, version strings). v3 adds a `welcome_hash` field -- an opaque server-side fingerprint. The client persists it per-node and offers it back on the next reconnect via a small `client_hello` frame:

```
[upgrade] Sec-WebSocket-Protocol: whyknot-v3

client -> server (FIRST FRAME):
  {"action":"client_hello","welcome_hash":"<cached-hash or null>","client_id":"<process-guid>"}

server -> client:
  EITHER (cache hit, small):
    {"action":"welcome_cached","protocol_version":3,"node":"node1","warp_active":true}
  OR (cache miss, full welcome with new hash to cache):
    {"action":"welcome", "protocol_version":3, "node":..., "engines":[...],
     "features":[..., "v3_compression", "welcome_hash_ack"], "yt_dlp_version":...,
     "server_version":..., "welcome_hash":"<new-fingerprint>"}
```

After the welcome, the resolve hot path is **byte-exact identical to v2** -- `resolve` request -> `resolved` / `fallback_native` response. v3 only changes the handshake.

## Cache file location

`%LOCALAPPDATA%Low\WKVRCProxy\v3_welcome_cache.json` -- same LocalLow state-root as `clean_exit.flag`, `codec-state.json`, etc. (see [[Logs and Diagnostics]]).

Per-host keying: current clients key the welcome cache on `proxy.whyknot.dev`; legacy clients and fallback discovery may still use `node1.whyknot.dev` or `node2.whyknot.dev`. Cache entries remain keyed on the hostname so mixed deployments do not cross-serve welcome state.

Atomic write via `<file>.new` -> `File.Move(overwrite:true)`. A crash mid-write leaves either the old or new file intact, never half-written.

## What v3.1 added (current)

**Asymmetric format negotiation.** Client -> server stays JSON over WebSocket Text frames; server -> client *post-welcome* picks JSON-Text or MessagePack-Binary based on what the client offered. Control frames (welcome / welcome_cached / client_hello) stay JSON either way for debuggability + first-byte simplicity.

The choice rides on the existing `client_hello` frame:

```
client -> server (FIRST FRAME, JSON-Text):
  {
    "action": "client_hello",
    "welcome_hash": "<cached or null>",
    "client_id": "<persistent guid>",
    "accept_formats": ["msgpack", "json"]   <- v3.1 NEW (preference-ordered)
  }

server -> client (welcome, JSON-Text):
  {
    "action": "welcome", ...,
    "negotiated_format": "msgpack"          <- v3.1 NEW. "json" or "msgpack".
  }
```

Server picks the first format from `accept_formats` it supports. Older servers (v2 / v3.0) ignore the unknown field and the response defaults to `json` -- universal fallback baseline. Choice is fixed for the connection's lifetime; no per-frame negotiation.

After the welcome, the **hot path** (`resolved` / `fallback_native` / `resolve_log`) arrives as MessagePack Binary frames when the server picked msgpack. Wire savings measured live: 60-72% smaller than JSON, ~67% faster to decode. Wrapper still receives JSON over the local pipe -- the watchdog transcodes msgpack -> JSON before the pipe write so the wrapper stays simple and small.

### Control frames stay JSON-Text always

A subset of frames is always JSON Text regardless of the negotiated format: `pong`, `ping`, `protocol_error`, `rate_limited`. A multi-hour reconnect storm in production traced to a server-side regression that routed pongs through the binary path on msgpack-negotiated connections; the watchdog's hot-path msgpack decoder didn't recognise them and default-discarded, the connection eventually idled out, the client reconnected, repeat. The current client recognises those four control actions on the binary dispatch path as defense-in-depth even though the server's invariant is to always send them as JSON-Text. If you add a new control-style frame (server -> client, low-frequency, not part of the resolve hot path), the rule is: send as JSON-Text, recognise on both Text and Binary paths client-side.

## Backward compat (v2 fallback)

When the server doesn't accept `whyknot-v3`:
- v2 servers don't recognize the subprotocol -> echo it back as null/empty.
- Cloudflare in some configs strips unrecognized WS subprotocol headers -> server never sees it.
- Older server builds before v3.0 deployed.

In all three cases:
- Client's `ShouldSendClientHello` returns false based on the echoed subprotocol.
- No `client_hello` is sent.
- Existing v2 path runs unchanged: server emits its plain welcome, client deserializes it, hot path operates as it has since v2.
- File-only `[mesh][v3] negotiated subprotocol=<none> v3=false` log line in `watchdog-<utc>.log` so the diagnostic is available without any console churn.

## What v3 does NOT do

- Doesn't change the resolve hot path. `resolve` request and `resolved` / `fallback_native` response shapes are identical to v2.
- Doesn't introduce a local HTTP proxy server (the parked Part E adapter design -- separate, larger work).
- Doesn't change the wrapper (`Tools/yt-dlp.exe`). v3 is a watchdog <-> mesh-server concern only.

## See also

- [[Engineering Standards]] -- wire-protocol contributor rules.
- `src/WKVRCProxy.Shared/Protocol.cs` -- DTO definitions, byte-exact with whyknot.dev's `MeshResolveProtocol.cs`.
- `src/WKVRCProxy/MeshClient.cs` -- connection lifecycle + dispatch.
- `src/WKVRCProxy/WelcomeCache.cs` -- cache persistence.
- `src/WKVRCProxy/MeshJsonContext.cs` -- source-gen JSON metadata for the v3 DTOs.
