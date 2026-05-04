using System.Text.Json.Serialization;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Source-generated JSON metadata for the watchdog's v3-handshake DTOs and
// the per-node welcome cache file. Used in place of reflection-based
// JsonSerializer.Serialize<T> / Deserialize<T> on the v3 path so:
//   * The v3 code is AOT-clean from day one (the watchdog itself isn't
//     AOT'd today — see project_size_optimization.md — but when the
//     watchdog AOT audit lands, the v3 path won't need a follow-up
//     refactor).
//   * Trim warnings (IL2026 / IL3050) stay quiet against this set even
//     under PublishTrimmed=true.
//
// Scope is deliberately narrow: only DTOs the v3 wire path or the local
// cache file touches. The existing v2 path's WelcomeFrame deserialize
// (MeshClient.cs DispatchFrameAsync, ActionWelcome case) still uses
// reflection — that's the watchdog AOT audit's job, not v3's. WelcomeFrame
// IS in this context too because the v3 path stores it on welcome receipt
// (cache hydration), but the v2 dispatch site keeps its reflection call
// to avoid changing v2 behaviour during v3 rollout.
[JsonSerializable(typeof(WelcomeFrame))]
[JsonSerializable(typeof(ClientHelloFrame))]
[JsonSerializable(typeof(WelcomeCachedFrame))]
[JsonSerializable(typeof(WelcomeCacheFile))]
[JsonSerializable(typeof(WelcomeCacheEntry))]
internal sealed partial class MeshJsonContext : JsonSerializerContext
{
}
