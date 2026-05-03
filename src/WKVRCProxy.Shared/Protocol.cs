// Wire types and constants shared between the watchdog client (WKVRCProxy.exe)
// and the patched yt-dlp.exe over the local named pipe, and between the watchdog
// and the WhyKnot mesh server over the WebSocket. Both sides serialize the same
// JSON shapes; only the transport differs.
using System.Text.Json.Serialization;

namespace WKVRCProxy.Shared;

public static class WireConstants
{
    public const string PipeName = "WKVRCProxy.resolve";

    public const string ActionResolve = "resolve";
    public const string ActionResolved = "resolved";
    public const string ActionFallbackNative = "fallback_native";
    public const string ActionResolveLog = "resolve_log";
    public const string ActionPing = "ping";
    public const string ActionPong = "pong";

    public const string FallbackAllConfigsFailed = "all_configs_failed";
    public const string FallbackDomainBlocked = "domain_blocked";
    public const string FallbackExtractorUnsupported = "extractor_unsupported";
    public const string FallbackInternalError = "internal_error";
    public const string FallbackDiscoveryInProgress = "discovery_in_progress";
    public const string FallbackServerUnreachable = "server_unreachable";
}

public sealed class ResolveRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = WireConstants.ActionResolve;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("player")] public string? Player { get; set; }
    [JsonPropertyName("maxHeight")] public int? MaxHeight { get; set; }
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
}
