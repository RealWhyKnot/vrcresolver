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
    public void HlsRewrite_DirectGooglevideoSegments_EmittedAsIs()
    {
        // Trust-list bypass: googlevideo segments don't need the wrap
        // because *.googlevideo.com is on AVPro's allowlist already.
        // Emitting them directly avoids the manifest-size blowup that
        // broke long-form playback.
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

        // Trust-listed segments emitted directly -- no wrap.
        Assert.DoesNotContain("http://localhost.youtube.com:", rewritten);
        Assert.Contains("https://r1.googlevideo.com/seg-1.ts", rewritten);
        Assert.Contains("https://r1.googlevideo.com/seg-2.ts", rewritten);

        // Non-segment lines preserved.
        Assert.Contains("#EXTM3U", rewritten);
        Assert.Contains("#EXT-X-VERSION:3", rewritten);
        Assert.Contains("#EXT-X-TARGETDURATION:6", rewritten);
        Assert.Contains("#EXTINF:5.005", rewritten);
        Assert.Contains("#EXT-X-ENDLIST", rewritten);
    }

    [Fact]
    public void HlsRewrite_ServerProxyWrappedTrustedSegments_Unwrapped()
    {
        // Real-world shape: server's RewriteHls wraps each segment as
        // `https://node1.whyknot.dev/api/proxy?url=<base64-of-googlevideo>`.
        // The listener unwraps and emits the inner googlevideo URL directly
        // because its host is on AVPro's trust list. Closes the manifest-
        // size blowup that fired this regression test in the first place.
        string innerUrl = "https://rr4---sn-nx57ynsl.googlevideo.com/videoplayback/expire/1778055157/itag/301/source/youtube";
        string innerB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        string serverWrapped = "https://node1.whyknot.dev/api/proxy?url=" + innerB64;

        string manifest =
            "#EXTM3U\n" +
            "#EXTINF:5.0,\n" +
            serverWrapped + "\n" +
            "#EXT-X-ENDLIST\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://node1.whyknot.dev/api/proxy?q=...", 51234);

        // Inner googlevideo URL emitted directly. No node1 wrapping
        // remains, no localhost gateway wrap remains.
        Assert.Contains(innerUrl, rewritten);
        Assert.DoesNotContain("node1.whyknot.dev/api/proxy", rewritten);
        Assert.DoesNotContain("http://localhost.youtube.com:", rewritten);
    }

    [Fact]
    public void HlsRewrite_NonAllowlistedHost_StillWrapped()
    {
        // example.com is NOT on AVPro's trust list, so the segment URL
        // continues to need the localhost.youtube.com gateway wrap.
        // Verifies the unwrap path is gated on the allowlist; doesn't
        // bypass for arbitrary hosts.
        string manifest =
            "#EXTM3U\n" +
            "#EXTINF:5.0,\n" +
            "https://example.com/segment.ts\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://example.com/m.m3u8", 51234);

        Assert.Contains("http://localhost.youtube.com:51234/play?target=", rewritten);
        Assert.DoesNotContain("https://example.com/segment.ts\n", rewritten);
    }

    [Fact]
    public void HlsRewrite_RelativeGooglevideoSegment_EmittedAsAbsoluteDirect()
    {
        // Relative segment URLs resolved against a googlevideo base URL
        // produce absolute googlevideo URLs, which are trust-listed and
        // emitted directly.
        string manifest =
            "#EXTM3U\n" +
            "#EXTINF:5.0,\n" +
            "seg-1.ts\n" +
            "#EXTINF:5.0,\n" +
            "subdir/seg-2.ts\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://r1.googlevideo.com/path/manifest.m3u8", 51234);

        // Relative URLs resolved + emitted directly.
        Assert.Contains("https://r1.googlevideo.com/path/seg-1.ts", rewritten);
        Assert.Contains("https://r1.googlevideo.com/path/subdir/seg-2.ts", rewritten);
        Assert.DoesNotContain("http://localhost.youtube.com:", rewritten);
    }

    [Fact]
    public void HlsRewrite_UriAttribute_TrustListAware()
    {
        // EXT-X-MAP (init segment) + EXT-X-MEDIA URI= attribute lines:
        // the URI gets the same trust-list-aware handling as bare segment
        // lines. Init segment on googlevideo emits direct; audio playlist
        // (relative) resolves to googlevideo and also emits direct.
        string manifest =
            "#EXTM3U\n" +
            "#EXT-X-MAP:URI=\"https://r1.googlevideo.com/init.mp4\",BYTERANGE=\"800@0\"\n" +
            "#EXT-X-MEDIA:TYPE=AUDIO,URI=\"audio-eng.m3u8\",GROUP-ID=\"audio\",NAME=\"English\"\n" +
            "#EXTINF:5.0,\n" +
            "seg.ts\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://r1.googlevideo.com/manifest.m3u8", 51234);

        // URI attributes emit direct googlevideo URLs.
        Assert.Contains("URI=\"https://r1.googlevideo.com/init.mp4\"", rewritten);
        Assert.Contains("URI=\"https://r1.googlevideo.com/audio-eng.m3u8\"", rewritten);
        // BYTERANGE preserved alongside the URI on the EXT-X-MAP line
        Assert.Contains("BYTERANGE=\"800@0\"", rewritten);
        // EXT-X-MEDIA attributes preserved
        Assert.Contains("GROUP-ID=\"audio\"", rewritten);
        Assert.Contains("NAME=\"English\"", rewritten);
        // Whole manifest stays free of the gateway wrap on this all-trusted set.
        Assert.DoesNotContain("http://localhost.youtube.com:", rewritten);
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
        // Defensive: bare lines that resolve against the manifest base
        // become a URL on a non-trust-listed host (example.com) and
        // therefore go through the wrap path -- the rewriter is intentionally
        // permissive about what counts as a segment; the cost of false-
        // positive wrap on non-allowlisted hosts is one 404 fetch when
        // AVPro tries to play it, not a parse failure.
        string manifest = "#EXTM3U\nrandom-non-url-text-that-isnt-a-segment\n";
        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://example.com/m.m3u8", 51234);
        Assert.Contains("http://localhost.youtube.com:51234/play?target=", rewritten);
    }

    [Theory]
    [InlineData("youtube.com", true)]
    [InlineData("www.youtube.com", true)]
    [InlineData("m.youtube.com", true)]
    [InlineData("music.youtube.com", true)]
    [InlineData("youtu.be", true)]
    [InlineData("rr4---sn-nx57ynsl.googlevideo.com", true)]
    [InlineData("manifest.googlevideo.com", true)]
    [InlineData("googlevideo.com", true)]
    [InlineData("vimeo.com", true)]
    [InlineData("player.vimeo.com", true)]
    [InlineData("www.twitch.tv", true)]
    [InlineData("video-weaver.lax03.hls.ttvnw.net", true)]
    [InlineData("video-edge-abc.lax03.abs.hls.ttvnw.net", true)]
    [InlineData("a1.sndcdn.com", true)]
    [InlineData("vrcdn.live", true)]
    [InlineData("stream.vrcdn.cloud", true)]
    [InlineData("vod-progressive.akamaized.net", true)]
    [InlineData("notyoutube.com", false)]
    [InlineData("youtube.com.evil.com", false)]
    [InlineData("example.com", false)]
    [InlineData("node1.whyknot.dev", false)]
    [InlineData("", false)]
    public void TrustedAvProHosts_IsTrusted_MatchesAllowlist(string host, bool expected)
    {
        // Pin the allowlist so a future "let's also trust X" change is a
        // visible diff alongside the test update. Substring-evil-host
        // negatives prevent a regression where a contributor switches
        // from suffix-match to substring-match.
        Assert.Equal(expected, TrustedAvProHosts.IsTrusted(host));
    }
}
