# Trust Gateway

The trust gateway is the local HTTP listener that lets AVPro accept resolved stream URLs in default-public VRChat worlds. It exists because of a constraint baked into VRChat's video player.

## The constraint

VRChat's AVPro layer ships with a hardcoded list of trusted hostnames. In worlds with "Allow Untrusted URLs" off (the default for public instances), AVPro rejects any URL whose host doesn't match an entry in that list. The list is small: `*.youtube.com`, `youtu.be`, `*.googlevideo.com`, `*.facebook.com`, `*.fbcdn.net`, `*.vimeo.com`, `*.twitch.tv`, a handful of others.

The resolver returns URLs of the form `https://node1.whyknot.dev/api/proxy?q=...`. `node1.whyknot.dev` is not on the trust list. AVPro silently rejects with `Loading failed` within ~1 second of opening, no real network fetch attempted.

## The fix

Hosts file entry on every install:

```
127.0.0.1   localhost.youtube.com   # WKVRCProxy
```

`localhost.youtube.com` matches the `*.youtube.com` allowlist pattern. The watchdog binds an HTTP listener on `127.0.0.1:{ephemeral-port}` and writes the port number to `%LOCALAPPDATA%Low\WKVRCProxy\relay_port.txt`. The patched yt-dlp wrapper reads the port and rewrites every resolved URL to:

```
http://localhost.youtube.com:{port}/play/<session>/manifest.<ext>?target=<base64-of-real-resolved-url>
```

The hosts entry routes the AVPro fetch to `127.0.0.1:{port}`. The listener decodes the base64, fetches the real URL via HttpClient, and streams bytes back to AVPro with the original headers preserved. The `<session>` path namespace gives relative playlist subrequests a stable local base path. AVPro sees a hostname it trusts and a working stream, never knows it took a detour.

## Request flow

```
AVPro:  GET http://localhost.youtube.com:51234/play/7f8a9b0c1d2e/manifest.m3u8?target=aHR0cHM6Ly9ub2RlMS53aHlrbm90LmRldi9hcGk...
        (Range: bytes=0-1023)
              |
              v  (DNS via hosts file)
              |
        127.0.0.1:51234
              |
              v
+-----------------------------+
| LocalRelayServer (watchdog) |  base64-decode target -> https://node1.whyknot.dev/api/proxy?q=...
| - HttpListener              |  HttpClient.SendAsync (forwards Range, If-*, User-Agent)
| - HttpClient                |  reads response headers
| - relative-prefix map       |  /play/<session>/seg.ts -> upstream base + seg.ts
+-----------------------------+  no manifest parsing or body rewriting
              |
              v
        Stream bytes back to AVPro with status + Content-Type + Content-Length + Content-Range mirrored.
```

## HLS handling

The client no longer parses or rewrites HLS manifests. WhyKnot.dev owns HLS compatibility, tier routing, and server-side transcode output.

When the first local URL includes `target=`, the listener records `/play/<session>/` against the upstream manifest directory. If the playlist body contains relative subresource URLs, AVPro resolves them under the same local namespace and the listener forwards them relative to that upstream directory.

Absolute playlist URLs are a server-side contract. The client will not rewrite them back onto `localhost.youtube.com`; WhyKnot.dev must emit client-compatible playlist bodies for the trust-gateway path.

## Failure modes

| Symptom | Cause |
|---|---|
| Watchdog logs `[relay] listening port 51234` then resolves succeed | Working as designed. |
| Watchdog logs `[relay][error] HttpListener.Start failed on port {port}` | Some other process is binding 127.0.0.1 on that port. The port file is deleted; wrapper falls through to emitting the raw server URL (today's behavior; works in trust-disabled worlds, fails in default-public). Restart the watchdog -- it picks a different ephemeral port. |
| Watchdog logs `[relay][warn] could not allocate ephemeral port` | Windows refused to allocate an ephemeral port at all. Rare. Restart the OS or check `netstat -an` for exhaustion. |
| Wrapper log shows `trust_gateway=passthrough` for every resolve | The port file is missing. Either the watchdog isn't running, or its listener didn't bind. Check the watchdog log. |
| AVPro still rejects with `Loading failed` despite the gateway running | Hosts entry got removed. The `HostsTicker` re-adds it within 60 seconds; wait or restart the watchdog. |

## What the gateway does NOT do

- Does not rewrite URLs the wrapper didn't see (e.g. URLs hand-typed in VRChat for hosts that VRChat itself recognizes via its own player URL handling -- those go straight to AVPro and we're not in the path).
- Does not modify request/response bodies. Manifests, binary mp4 / mp3 / webm, and segment bytes pass through verbatim.
- Does not attempt to handle DRM. Encrypted streams go through whatever DRM negotiation AVPro does directly with the upstream.
- Does not require VRChat to be running. The listener binds at watchdog startup and stays up until shutdown.

## Phase 1 is HTTP-only; HTTPS is parked

The listener serves plain HTTP on a non-443 port. AVPro accepts plain `http://` for hostnames matching its allowlist; the legacy implementation proved this in production for years and the current Phase 1 implementation has been verified in the wild. HTTPS adds:

- Per-machine self-signed certificate generation
- Trust store install (`LocalMachine\Root`)
- `netsh http add sslcert` URL-prefix binding
- Certificate renewal flow in the updater
- Certificate cleanup in the uninstaller

Roughly 8 dev-days of work for hardening that doesn't change correctness for any user we've observed. The plan stays drafted; resumption triggers are concrete:

1. AVPro starts requiring HTTPS for trust-list hostnames. Symptom: `Loading failed` within ~1 s on the localhost.youtube.com URL specifically, no body bytes fetched, distinguished from listener failure by curl-ing the same URL outside VRChat and getting 200.
2. VRChat or AVPro adds certificate pinning for trust-list hostnames. Symptom: TLS handshake errors in `output_log_*.txt`.
3. A security audit demands TLS-everywhere on the watchdog's surfaces.
4. A user reports public-instance failure with a clear log capture showing the URL passing the trust check but failing for a TLS-related reason.

If any of those fire, see the parked design notes in the project memory.

## Source pointers

- `src/WKVRCProxy/LocalRelayServer.cs` -- the HttpListener loop, request handler, relative playlist namespace map, idle timeout + disconnect propagation.
- `src/WKVRCProxy/RelayPortManager.cs` -- ephemeral port allocation, port file persistence, reuse across restarts.
- `src/WKVRCProxy/HostsManager.cs` + `src/WKVRCProxy/HostsTicker.cs` -- the hosts entry add/remove + 60-second re-check ticker.
- `src/WKVRCProxy.YtDlp/Program.cs::TryWrapForTrustGateway` -- the wrapper-side URL rewrite that emits the gateway URL.
- `src/WKVRCProxy.Tests/LocalRelayServerTests.cs` -- regression tests for target encoding and relative namespace resolution.
