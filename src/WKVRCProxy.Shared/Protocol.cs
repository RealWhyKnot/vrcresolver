// Wire types and constants shared between the watchdog client (WKVRCProxy.exe)
// and the patched yt-dlp.exe over the local named pipe, and between the watchdog
// and the WhyKnot mesh server over the WebSocket. Both sides serialize the same
// JSON shapes; only the transport differs.
//
// Schema versioning:
//   v1 — original shape (action/id/url/player/maxHeight on request; action/id/
//        url/engine/config/reason/message on response).
//   v2 — adds optional snake_case request fields (protocol_version, correlation_id,
//        accept_protocols, accept_codecs, max_audio_channels, vrchat_format_arg)
//        and optional response fields (container, video_codec, audio_codec,
//        protocol, audio_channels, bytes_estimate, expires_at). Plus a server-
//        emitted "welcome" frame on WS connect carrying server capabilities.
//
// Backward-compat: every DTO uses [JsonExtensionData] so unknown fields round-trip
// across the watchdog without loss. v3 fields the server adds later will reach
// the consumer (patched yt-dlp / mesh server) untouched.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WKVRCProxy.Shared;

public static class WireConstants
{
    public const string PipeName = "WKVRCProxy.resolve";

    // Wire shape this client speaks. Sent in the optional request
    // protocol_version field once welcome confirms the server is v2-capable.
    public const int ClientProtocolVersion = 2;

    // Action vocabulary
    public const string ActionResolve = "resolve";
    public const string ActionResolved = "resolved";
    public const string ActionFallbackNative = "fallback_native";
    public const string ActionResolveLog = "resolve_log";
    public const string ActionPing = "ping";
    public const string ActionPong = "pong";
    public const string ActionWelcome = "welcome";
    // Client → server feedback. Fire-and-forget; server must not respond
    // and must tolerate parse errors. See VrcLogMonitor for emission
    // semantics and PlaybackFeedbackKind for the value vocabulary.
    public const string ActionPlaybackFeedback = "playback_feedback";

    public const string PlaybackFeedbackLoadFailure = "load_failure";
    public const string PlaybackFeedbackSilentStall = "silent_stall";

    // v2 field names (snake_case, mirror server constants verbatim)
    public const string FieldProtocolVersion = "protocol_version";
    public const string FieldAcceptProtocols = "accept_protocols";
    public const string FieldAcceptCodecs = "accept_codecs";
    public const string FieldMaxAudioChannels = "max_audio_channels";
    public const string FieldVrchatFormatArg = "vrchat_format_arg";
    public const string FieldCorrelationId = "correlation_id";

    // Player vocabulary. Spec is case-sensitive; only "avpro" and "unity" are
    // valid on the wire. PlayerUnknown is a watchdog-internal tag for log
    // lines when the patched yt-dlp didn't supply a player — never sent.
    public const string PlayerAvPro = "avpro";
    public const string PlayerUnity = "unity";
    public const string PlayerUnknown = "unknown";

    // Fallback reasons (existing v1)
    public const string FallbackAllConfigsFailed = "all_configs_failed";
    public const string FallbackDomainBlocked = "domain_blocked";
    public const string FallbackExtractorUnsupported = "extractor_unsupported";
    public const string FallbackInternalError = "internal_error";
    public const string FallbackDiscoveryInProgress = "discovery_in_progress";
    public const string FallbackServerUnreachable = "server_unreachable";

    // v2 fallback reasons. Both must NOT trigger a native-yt-dlp retry on the
    // patched-binary side: native would hit the same wall.
    //   - unity_unsupported_format: server couldn't produce a Unity-playable
    //     stream from any candidate; user should try AVPro.
    //   - warp_down: server's WARP egress is unhealthy; transient — user can
    //     retry in a few seconds, or another node may be healthy.
    public const string ReasonUnityUnsupportedFormat = "unity_unsupported_format";
    public const string ReasonWarpDown = "warp_down";

    // Default request profiles (server applies these when client omits the
    // matching field). Mirrored here for client-side logging clarity.
    public static readonly string[] AvProAcceptProtocols = { "http", "hls", "dash" };
    public static readonly string[] UnityAcceptProtocols = { "http" };
    public static readonly string[] AvProAcceptCodecs =
        { "h264", "h265", "vp9", "av1", "aac", "opus", "mp3", "ac3", "eac3" };
    public static readonly string[] UnityAcceptCodecs = { "h264", "aac" };
    public const int AvProMaxAudioChannels = 8;
    public const int UnityMaxAudioChannels = 2;
}

public sealed class ResolveRequest
{
    // v1 fields (camelCase preserved for backward compat with the existing
    // patched yt-dlp). Server still requires these.
    [JsonPropertyName("action")] public string Action { get; set; } = WireConstants.ActionResolve;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("player")] public string? Player { get; set; }
    [JsonPropertyName("maxHeight")] public int? MaxHeight { get; set; }

    // v2 fields (snake_case). Optional — send only when the watchdog has
    // confirmed the server speaks v2 via the welcome frame, OR pass through
    // verbatim if the patched yt-dlp populated them on the pipe request.
    [JsonPropertyName("protocol_version")] public int? ProtocolVersion { get; set; }
    [JsonPropertyName("correlation_id")] public string? CorrelationId { get; set; }
    [JsonPropertyName("accept_protocols")] public string[]? AcceptProtocols { get; set; }
    [JsonPropertyName("accept_codecs")] public string[]? AcceptCodecs { get; set; }
    [JsonPropertyName("max_audio_channels")] public int? MaxAudioChannels { get; set; }
    [JsonPropertyName("vrchat_format_arg")] public string? VrchatFormatArg { get; set; }

    // Forward-compat: any field name we don't statically know (e.g., a v3 field)
    // round-trips through the watchdog from pipe → WS without loss. Both
    // serializer and deserializer respect this when reflecting over the type.
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ResolveResponse
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("engine")] public string? Engine { get; set; }
    [JsonPropertyName("config")] public string? Config { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }

    // v2 fields. Server emits these only when the request's protocol_version
    // was >= 2. "unknown" is a valid value (server couldn't determine), not
    // an error — treat it as v1 (no constraint info).
    [JsonPropertyName("container")] public string? Container { get; set; }
    [JsonPropertyName("video_codec")] public string? VideoCodec { get; set; }
    [JsonPropertyName("audio_codec")] public string? AudioCodec { get; set; }
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
    [JsonPropertyName("audio_channels")] public int? AudioChannels { get; set; }
    [JsonPropertyName("bytes_estimate")] public long? BytesEstimate { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// Welcome frame: server → client, once per WS connection, ~50ms after accept.
// Carries server capabilities so the client knows whether to use v2 fields.
// Required: action, protocol_version, node, engines, features.
// Optional: warp_active, yt_dlp_version, server_version.
public sealed class WelcomeFrame
{
    [JsonPropertyName("action")] public string Action { get; set; } = WireConstants.ActionWelcome;
    [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }
    [JsonPropertyName("node")] public string? Node { get; set; }
    [JsonPropertyName("warp_active")] public bool? WarpActive { get; set; }
    [JsonPropertyName("engines")] public string[]? Engines { get; set; }
    [JsonPropertyName("features")] public string[]? Features { get; set; }
    [JsonPropertyName("yt_dlp_version")] public string? YtDlpVersion { get; set; }
    [JsonPropertyName("server_version")] public string? ServerVersion { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
