# Relay Server & Trust Bypass

VRChat's AVPro player ships with a hardcoded **trusted-URL list**. URLs whose host doesn't match are silently rejected with `[AVProVideo] Error: Loading failed.` There is no in-game setting to bypass it. The relay is how WKVRCProxy works around it without modifying VRChat itself.

Sources: [`RelayServer.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/RelayServer.cs), [`HostsManager.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/HostsManager.cs).

## The hosts-file trick

On first run, `HostsManager` adds:

```
127.0.0.1 localhost.youtube.com
```

to `%WINDIR%\System32\drivers\etc\hosts` (UAC required; if declined, `BypassHostsSetupDeclined` is set in config and the prompt isn't repeated).

That single entry is the load-bearing piece. AVPro trusts `*.youtube.com`. Once the hosts file maps `localhost.youtube.com` to `127.0.0.1`, the engine can rewrite **any** resolved URL as

```
http://localhost.youtube.com:{relay_port}/play?target=<base64(real_url)>
```

…and AVPro happily accepts it (the host *looks* like YouTube). The local relay then fetches the real upstream URL and streams it back to AVPro.

## Wrap by default; deny-list the exceptions

The relay wrap is the **default behaviour** — every URL gets wrapped unless the host is on one of two lists:

1. **VRChat's trusted-host list** (hardcoded in `_vrchatTrustedHostPatterns` in `ResolutionEngine.cs`). These hosts play pristine.
2. **`NativeAvProUaHosts`** (`AppConfig.NativeAvProUaHosts`, default `["vr-m.net"]`). These are hosts that gate on AVPro's `UnityPlayer/...` UA — wrapping them through the relay corrupts the User-Agent (the relay forwards either configured headers or the request's incoming headers, neither matches the live AVPro UA exactly enough).

> **Don't add to the deny-list speculatively.** Each entry should be backed by logs showing the URL works pristine but fails wrapped. The default narrow deny-list is intentional.

## Relay rules: `proxy-rules.json`

Source: [`ProxyRuleManager.cs`](https://github.com/RealWhyKnot/WKVRCProxy/blob/main/src/WKVRCProxy.Core/Services/ProxyRuleManager.cs). Per-domain knobs the relay uses when handling requests:

- Forwarded headers (allowlist)
- UA override
- PO-token injection
- `curl-impersonate` toggle (use Chrome TLS fingerprint for upstream fetches)
- Session-cache replay (paired with `BrowserExtractService` cookies/headers when bypassing JS challenges)

Rules live in `proxy-rules.json` next to the exe. Custom edits are preserved across version upgrades.

## Format-rejection detection

AVPro doesn't always log `Loading failed` cleanly when it gets a format it can't decode — sometimes it just closes the relay connection after a short prefix. The relay treats a connection abort with **<256 KB received** as a probable format rejection. Three or more such aborts on the same target URL within 30 seconds triggers the same `PlaybackFailed` demotion path as an explicit `Loading failed` log line. See [[Resolution Cascade]] for the demotion mechanics.

## Smoothness instrumentation

`EnableRelaySmoothnessDebug` (default true) logs HLS segment TTFB and throughput numbers at debug/warn levels. Useful when chasing "video starts then stutters" reports — usually means the relay's upstream fetch is slower than AVPro's playback rate.

## Log-flooding protection

The relay can fire the `Relaying:` INFO line dozens of times per second for an HLS stream (each segment is a request). To keep the Logs view readable, repeated `Relaying:` lines for the same target URL within a 30-second sliding window are suppressed. The first line still goes through; later ones are accumulated and summarized.

## Port allocation

`RelayPortManager` picks a free port at startup and stores it in two places:

- `%LOCALAPPDATA%\WKVRCProxy\relay_port.dat`
- `{vrcToolsDir}\relay_port.dat` (so the Redirector can read it without touching AppData)

The IPC server uses the same dual-write strategy with `ipc_port.dat`. See [[Runtime State]].
