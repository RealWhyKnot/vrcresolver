using System.Text.Json.Serialization;
using VrcResolver.Shared;

namespace VrcResolver.YtDlp;

// Source-generated JSON metadata for the only two DTOs the wrapper touches on
// the pipe. Used in place of reflection-based JsonSerializer.Serialize<T> /
// Deserialize<T> so the wrapper can publish under PublishAot=true with
// TrimMode=full — without this context the trimmer + AOT compiler can't see
// which property accessors are reachable, the JSON code path falls back to
// runtime reflection, and AOT publish bails with IL2026/IL3050.
//
// Scope: ResolveRequest (wrapper -> watchdog) and ResolveResponse
// (watchdog -> wrapper). The wrapper does not handle WelcomeFrame or any other
// Protocol DTO; including unused types here would bloat the AOT binary.
//
// v3.2: WrapperEventNotify (wrapper -> watchdog) is the og-fallback
// notification frame. Sent on a fresh pipe connection just before the
// wrapper execs yt-dlp-og.exe; fire-and-forget. Surfaces the fallback
// fact on the watchdog console.
[JsonSerializable(typeof(ResolveRequest))]
[JsonSerializable(typeof(ResolveResponse))]
[JsonSerializable(typeof(WrapperEventNotify))]
internal sealed partial class WrapperJsonContext : JsonSerializerContext
{
}
