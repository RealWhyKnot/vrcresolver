using System.Text.Json;
using WKVRCProxy.Shared;
using Xunit;

namespace WKVRCProxy.Tests;

// Verifies the wire-protocol DTOs deserialize and re-serialize without
// losing v2 fields or unknown extension data. Regressions here would
// silently break v3+ forward-compat — invisible until a server-side
// field gets dropped on the floor.
public class ProtocolRoundTripTests
{
    [Fact]
    public void ResolveRequest_v2_fields_round_trip()
    {
        const string json = """
        {
          "action": "resolve",
          "id": "abc123",
          "url": "https://www.youtube.com/watch?v=foo",
          "player": "unity",
          "maxHeight": 1080,
          "protocol_version": 2,
          "correlation_id": "user-42",
          "accept_protocols": ["http"],
          "accept_codecs": ["h264", "aac"],
          "max_audio_channels": 2,
          "vrchat_format_arg": "bestvideo[height<=1080][vcodec^=avc1]+bestaudio/best"
        }
        """;
        var req = JsonSerializer.Deserialize<ResolveRequest>(json);
        Assert.NotNull(req);
        Assert.Equal("resolve", req!.Action);
        Assert.Equal("abc123", req.Id);
        Assert.Equal("unity", req.Player);
        Assert.Equal(1080, req.MaxHeight);
        Assert.Equal(2, req.ProtocolVersion);
        Assert.Equal("user-42", req.CorrelationId);
        Assert.Equal(new[] { "http" }, req.AcceptProtocols);
        Assert.Equal(new[] { "h264", "aac" }, req.AcceptCodecs);
        Assert.Equal(2, req.MaxAudioChannels);
        Assert.Equal("bestvideo[height<=1080][vcodec^=avc1]+bestaudio/best", req.VrchatFormatArg);
    }

    [Fact]
    public void ResolveRequest_unknown_fields_round_trip_via_extension_data()
    {
        const string json = """
        {
          "action": "resolve",
          "id": "abc",
          "url": "https://x",
          "player": "avpro",
          "maxHeight": 1080,
          "future_v3_field": {"nested": true, "values": [1,2,3]},
          "another_unknown": "preserve me"
        }
        """;
        var req = JsonSerializer.Deserialize<ResolveRequest>(json);
        Assert.NotNull(req);
        Assert.NotNull(req!.Extra);
        Assert.True(req.Extra!.ContainsKey("future_v3_field"));
        Assert.True(req.Extra.ContainsKey("another_unknown"));

        // Re-serialize and verify the unknowns survive the round trip.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(req);
        using var doc = JsonDocument.Parse(bytes);
        Assert.True(doc.RootElement.TryGetProperty("future_v3_field", out var fv3));
        Assert.Equal(JsonValueKind.Object, fv3.ValueKind);
        Assert.True(fv3.TryGetProperty("nested", out var nested));
        Assert.True(nested.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("another_unknown", out var au));
        Assert.Equal("preserve me", au.GetString());
    }

    [Fact]
    public void ResolveResponse_v2_fields_round_trip()
    {
        const string json = """
        {
          "action": "resolved",
          "id": "abc",
          "url": "https://signed-cdn/...",
          "engine": "yt-dlp",
          "config": "default",
          "container": "mp4",
          "video_codec": "h264",
          "audio_codec": "aac",
          "protocol": "http",
          "audio_channels": 2,
          "bytes_estimate": 12345678,
          "expires_at": "2026-01-01T00:00:00Z"
        }
        """;
        var resp = JsonSerializer.Deserialize<ResolveResponse>(json);
        Assert.NotNull(resp);
        Assert.Equal("mp4", resp!.Container);
        Assert.Equal("h264", resp.VideoCodec);
        Assert.Equal("aac", resp.AudioCodec);
        Assert.Equal("http", resp.Protocol);
        Assert.Equal(2, resp.AudioChannels);
        Assert.Equal(12345678L, resp.BytesEstimate);
        Assert.Equal("2026-01-01T00:00:00Z", resp.ExpiresAt);
    }

    [Fact]
    public void WelcomeFrame_round_trips_with_all_fields_and_extras()
    {
        const string json = """
        {
          "action": "welcome",
          "protocol_version": 2,
          "node": "node1",
          "warp_active": true,
          "engines": ["yt-dlp", "streamlink", "passthrough"],
          "features": ["unity_format_selection","accept_protocols","accept_codecs"],
          "yt_dlp_version": "2026.10.31",
          "server_version": "2026.5.4-canary",
          "future_capability": "respect"
        }
        """;
        var w = JsonSerializer.Deserialize<WelcomeFrame>(json);
        Assert.NotNull(w);
        Assert.Equal(2, w!.ProtocolVersion);
        Assert.Equal("node1", w.Node);
        Assert.True(w.WarpActive);
        Assert.Equal(3, w.Engines!.Length);
        Assert.Equal(3, w.Features!.Length);
        Assert.Equal("2026.10.31", w.YtDlpVersion);
        Assert.Equal("2026.5.4-canary", w.ServerVersion);
        Assert.NotNull(w.Extra);
        Assert.True(w.Extra!.ContainsKey("future_capability"));
    }

    [Fact]
    public void WireConstants_match_server_spec_strings()
    {
        // These are the constants the server-side audit explicitly listed
        // as needing to be byte-exact. A typo in either side breaks the
        // wire silently — easy unit-test guard.
        Assert.Equal("welcome", WireConstants.ActionWelcome);
        Assert.Equal("protocol_version", WireConstants.FieldProtocolVersion);
        Assert.Equal("accept_protocols", WireConstants.FieldAcceptProtocols);
        Assert.Equal("accept_codecs", WireConstants.FieldAcceptCodecs);
        Assert.Equal("max_audio_channels", WireConstants.FieldMaxAudioChannels);
        Assert.Equal("vrchat_format_arg", WireConstants.FieldVrchatFormatArg);
        Assert.Equal("correlation_id", WireConstants.FieldCorrelationId);
        Assert.Equal("unity_unsupported_format", WireConstants.ReasonUnityUnsupportedFormat);
        Assert.Equal("warp_down", WireConstants.ReasonWarpDown);
        Assert.Equal("avpro", WireConstants.PlayerAvPro);
        Assert.Equal("unity", WireConstants.PlayerUnity);
        Assert.Equal(2, WireConstants.ClientProtocolVersion);
    }
}
