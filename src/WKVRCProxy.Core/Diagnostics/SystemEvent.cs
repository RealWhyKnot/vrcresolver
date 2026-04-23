using System;
using System.Text.Json.Serialization;

namespace WKVRCProxy.Core.Diagnostics;

public enum SystemEventType
{
    Log,
    Status,
    Relay,
    Prompt,
    Health,
    Error,
    // Raised when the playback-feedback loop demotes a strategy that returned a URL AVPro refused
    // (trust list, codec, unreachable). UI renders this as a dismissable chip so the user can see
    // which strategy/host pair fell out of the fast-path without parsing log lines.
    StrategyDemoted
}

public class SystemEvent
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter<SystemEventType>))]
    public SystemEventType Type { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("sourceModule")]
    public string SourceModule { get; set; } = "";

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}
