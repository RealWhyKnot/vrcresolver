using System.Text.Json;
using WKVRCProxy.Shared;
using Xunit;

namespace WKVRCProxy.Tests;

// Wire-shape regression tests for the v3 handshake DTOs. These guard
// against the kind of accidental rename / case-flip / serialization-attr
// drift that would silently desync the client from whyknot.dev's
// MeshResolveProtocol. Pair-test these against the server's equivalent
// snapshot tests when whyknot.dev's v3.0 lands.
public class V3ProtocolTests
{
    [Fact]
    public void ClientHelloFrame_serializes_with_null_hash()
    {
        var hello = new ClientHelloFrame
        {
            WelcomeHash = null,
            ClientId = "abc123",
        };
        string json = JsonSerializer.Serialize(hello);
        // Server treats welcome_hash=null as "no cache, send full welcome".
        // The field MUST be present on the wire so the server can
        // distinguish "field omitted" (older client, undefined behaviour)
        // from "field explicitly null" (v3 client with no cached welcome).
        Assert.Contains("\"welcome_hash\":null", json);
        Assert.Contains("\"action\":\"client_hello\"", json);
        Assert.Contains("\"client_id\":\"abc123\"", json);
    }

    [Fact]
    public void ClientHelloFrame_serializes_with_hash()
    {
        var hello = new ClientHelloFrame
        {
            WelcomeHash = "deadbeef0123",
            ClientId = "xyz",
        };
        string json = JsonSerializer.Serialize(hello);
        Assert.Contains("\"welcome_hash\":\"deadbeef0123\"", json);
    }

    [Fact]
    public void ClientHelloFrame_round_trip_preserves_extras()
    {
        // Forward-compat: a future v3.x server might reflect extra fields
        // we don't statically know about. JsonExtensionData should
        // round-trip them so a future agent reading the watchdog log
        // can still see what the server saw.
        string json = "{\"action\":\"client_hello\",\"welcome_hash\":\"abc\",\"client_id\":\"c\",\"future_field\":42}";
        var parsed = JsonSerializer.Deserialize<ClientHelloFrame>(json);
        Assert.NotNull(parsed);
        Assert.Equal("abc", parsed!.WelcomeHash);
        Assert.NotNull(parsed.Extra);
        Assert.True(parsed.Extra!.ContainsKey("future_field"));
    }

    [Fact]
    public void WelcomeCachedFrame_deserialize_minimal()
    {
        // Server SHOULD always send protocol_version + node, but we want
        // missing fields to land as defaults rather than throwing — a
        // brittle parse here would re-trigger the v2 fallback path on
        // benign server-side schema drift.
        string json = "{\"action\":\"welcome_cached\",\"protocol_version\":3}";
        var f = JsonSerializer.Deserialize<WelcomeCachedFrame>(json);
        Assert.NotNull(f);
        Assert.Equal(3, f!.ProtocolVersion);
        Assert.Null(f.Node);
        Assert.Null(f.WarpActive);
    }

    [Fact]
    public void WelcomeCachedFrame_deserialize_with_warp_active()
    {
        string json = "{\"action\":\"welcome_cached\",\"protocol_version\":3,\"node\":\"node1\",\"warp_active\":true}";
        var f = JsonSerializer.Deserialize<WelcomeCachedFrame>(json);
        Assert.NotNull(f);
        Assert.Equal("node1", f!.Node);
        Assert.True(f.WarpActive);
    }

    [Fact]
    public void WelcomeFrame_with_welcome_hash_field_round_trips()
    {
        // v3 servers emit welcome_hash inside the full welcome on cache
        // miss. The client persists it for next reconnect.
        var welcome = new WelcomeFrame
        {
            ProtocolVersion = 3,
            Node = "node1",
            Engines = new[] { "yt-dlp" },
            Features = new[] { WireConstants.FeatureV3Compression, WireConstants.FeatureWelcomeHashAck },
            WelcomeHash = "fingerprint",
            ServerVersion = "2026.5.4.7-3F2A",
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(welcome);
        var parsed = JsonSerializer.Deserialize<WelcomeFrame>(bytes);
        Assert.NotNull(parsed);
        Assert.Equal("fingerprint", parsed!.WelcomeHash);
        Assert.Equal(3, parsed.ProtocolVersion);
        Assert.Contains(WireConstants.FeatureV3Compression, parsed.Features!);
    }

    [Fact]
    public void WelcomeFrame_v2_payload_round_trips_without_welcome_hash()
    {
        // Backward compat: a v2 server's welcome doesn't include
        // welcome_hash. Deserialize must produce welcome_hash=null with
        // no thrown exception — the v3 client treats null hash as "this
        // is a v2 welcome, don't try to cache".
        string json = "{\"action\":\"welcome\",\"protocol_version\":2,\"node\":\"node1\",\"engines\":[\"yt-dlp\"],\"features\":[]}";
        var parsed = JsonSerializer.Deserialize<WelcomeFrame>(json);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.WelcomeHash);
        Assert.Equal(2, parsed.ProtocolVersion);
    }

    [Theory]
    [InlineData("whyknot-v3", true)]
    [InlineData(null, false)]              // v2 server / proxy stripped header
    [InlineData("", false)]                // server returned empty
    [InlineData("whyknot-v2", false)]      // hypothetical older negotiation
    [InlineData("Whyknot-V3", false)]      // case-flip drift detection (we're Ordinal, NOT OrdinalIgnoreCase)
    [InlineData("whyknot-v3 ", false)]     // trailing space
    public void ShouldSendClientHello_OnlyExactSubprotocolMatch(string? negotiated, bool expected)
    {
        // Subprotocol mismatch → client falls back to v2 path: no
        // client_hello, just wait for plain welcome. Pure helper so the
        // fallback decision is testable without a real ClientWebSocket.
        Assert.Equal(expected, MeshClient.ShouldSendClientHello(negotiated));
    }

    [Fact]
    public void WireConstants_v3_strings_match_server_spec()
    {
        // Byte-exact constants — must mirror whyknot.dev's
        // MeshResolveProtocol.cs. A casing flip here would silently desync
        // the entire v3 handshake.
        Assert.Equal("whyknot-v3", WireConstants.SubprotocolV3);
        Assert.Equal("client_hello", WireConstants.ActionClientHello);
        Assert.Equal("welcome_cached", WireConstants.ActionWelcomeCached);
        Assert.Equal("welcome_hash", WireConstants.FieldWelcomeHash);
        Assert.Equal("v3_compression", WireConstants.FeatureV3Compression);
        Assert.Equal("welcome_hash_ack", WireConstants.FeatureWelcomeHashAck);
        Assert.Equal(3, WireConstants.ClientProtocolVersion);
    }
}
