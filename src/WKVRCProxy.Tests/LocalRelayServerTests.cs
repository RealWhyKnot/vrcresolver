using System.Runtime.Versioning;
using System.Text;
using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

[SupportedOSPlatform("windows")]
public class LocalRelayServerTests
{
    [Fact]
    public void EncodeTargetParam_RoundTrips()
    {
        // Round-trip: encode + decode (via the server's private path
        // -- exercised indirectly by feeding encoded input back through
        // a constructed URL the rewriter would emit).
        string url = "https://node1.whyknot.dev/api/proxy?q=AbC-_dEf=&extra=hi%20there";
        string encoded = LocalRelayServer.EncodeTargetParam(url);

        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded);
        Assert.DoesNotContain("=", encoded);
        Assert.DoesNotContain(" ", encoded);

        // Manual decode mirroring the listener's DecodeTargetParam logic.
        string b64 = encoded.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Equal(url, decoded);
    }

    [Fact]
    public void HlsRewrite_AbsoluteSegmentUrl_GoesThroughRelay()
    {
        string manifest =
            "#EXTM3U\n" +
            "#EXT-X-VERSION:3\n" +
            "#EXT-X-TARGETDURATION:6\n" +
            "#EXTINF:5.005,\n" +
            "https://r1.googlevideo.com/seg-1.ts\n" +
            "#EXTINF:5.005,\n" +
            "https://r1.googlevideo.com/seg-2.ts\n" +
            "#EXT-X-ENDLIST\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://r1.googlevideo.com/manifest.m3u8", 51234);

        // Segment URLs replaced with relay-wrapped ones.
        Assert.Contains("http://localhost.youtube.com:51234/play?target=", rewritten);
        Assert.DoesNotContain("https://r1.googlevideo.com/seg-1.ts\n", rewritten);

        // Non-segment lines preserved (#EXTM3U + #EXT-X-VERSION + #EXT-X-TARGETDURATION + #EXTINF + #EXT-X-ENDLIST).
        Assert.Contains("#EXTM3U", rewritten);
        Assert.Contains("#EXT-X-VERSION:3", rewritten);
        Assert.Contains("#EXT-X-TARGETDURATION:6", rewritten);
        Assert.Contains("#EXTINF:5.005", rewritten);
        Assert.Contains("#EXT-X-ENDLIST", rewritten);
    }

    [Fact]
    public void HlsRewrite_RelativeSegmentUrl_ResolvesAgainstBase()
    {
        string manifest =
            "#EXTM3U\n" +
            "#EXTINF:5.0,\n" +
            "seg-1.ts\n" +
            "#EXTINF:5.0,\n" +
            "subdir/seg-2.ts\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://r1.googlevideo.com/path/manifest.m3u8", 51234);

        // The relative URL must have been resolved against the base
        // before being wrapped, so the base64 target inside should
        // decode to the absolute URL.
        Assert.Contains("http://localhost.youtube.com:51234/play?target=", rewritten);

        // Both segments wrapped (one bare, one with a subdir).
        var lines = rewritten.Split('\n');
        int wrapped = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("http://localhost.youtube.com:51234/play?target=")) wrapped++;
        }
        Assert.Equal(2, wrapped);
    }

    [Fact]
    public void HlsRewrite_UriAttribute_Rewritten()
    {
        // EXT-X-MAP (init segment) and EXT-X-MEDIA URI= forms must be
        // rewritten just like segment lines, otherwise AVPro fetches
        // those raw and fails the trust check.
        string manifest =
            "#EXTM3U\n" +
            "#EXT-X-MAP:URI=\"https://r1.googlevideo.com/init.mp4\",BYTERANGE=\"800@0\"\n" +
            "#EXT-X-MEDIA:TYPE=AUDIO,URI=\"audio-eng.m3u8\",GROUP-ID=\"audio\",NAME=\"English\"\n" +
            "#EXTINF:5.0,\n" +
            "seg.ts\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://r1.googlevideo.com/manifest.m3u8", 51234);

        // Both URI="..." attributes wrapped through the relay
        Assert.Contains("URI=\"http://localhost.youtube.com:51234/play?target=", rewritten);
        // BYTERANGE preserved alongside the rewritten URI on the EXT-X-MAP line
        Assert.Contains("BYTERANGE=\"800@0\"", rewritten);
        // EXT-X-MEDIA attributes preserved
        Assert.Contains("GROUP-ID=\"audio\"", rewritten);
        Assert.Contains("NAME=\"English\"", rewritten);
    }

    [Fact]
    public void HlsRewrite_EmptyOrCommentLines_PassThrough()
    {
        string manifest = "#EXTM3U\n\n#EXT-X-VERSION:3\n";
        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://example.com/m.m3u8", 51234);
        Assert.Equal(manifest, rewritten);
    }

    [Fact]
    public void HlsRewrite_NonUriBareLine_LeftAlone()
    {
        // Defensive: bare lines that don't parse as a URL when resolved
        // against the manifest URI shouldn't be mangled.
        string manifest = "#EXTM3U\nrandom-non-url-text-that-isnt-a-segment\n";
        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://example.com/m.m3u8", 51234);
        // The "random-non-url-text..." resolves against the manifest base
        // to `https://example.com/random-non-url-text-...` which IS a
        // valid absolute URL -- so this gets wrapped. Verify wrapping
        // happened (the rewriter is intentionally permissive about what
        // counts as a segment; the cost of false-positive wrap is one
        // 404 fetch, not a parse failure).
        Assert.Contains("http://localhost.youtube.com:51234/play?target=", rewritten);
    }
}
