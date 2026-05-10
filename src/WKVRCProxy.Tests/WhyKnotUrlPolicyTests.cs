using WKVRCProxy.Shared;
using Xunit;

namespace WKVRCProxy.Tests;

public class WhyKnotUrlPolicyTests
{
    [Theory]
    [InlineData("https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=abc", true)]
    [InlineData("https://whyknot.dev/api/proxy/lazy-hls/wk_abc/index.m3u8", true)]
    [InlineData("https://node2.whyknot.dev/api/popcorn/proxy/manifest.m3u8?clientId=c&index=1", true)]
    [InlineData("https://node2.whyknot.dev/api/popcorn/proxy/seg.ts?clientId=c&url=abc", true)]
    [InlineData("https://node1.whyknot.dev/api/restream/shared-status", false)]
    [InlineData("https://example.com/api/proxy/manifest.m3u8?q=abc", false)]
    [InlineData("https://notwhyknot.dev/api/proxy/manifest.m3u8?q=abc", false)]
    [InlineData("not a url", false)]
    public void IsWhyKnotPlaybackProxyUrl_AcceptsOnlyFirstPartyPlaybackProxyShapes(
        string url,
        bool expected)
    {
        Assert.Equal(expected, WhyKnotUrlPolicy.IsWhyKnotPlaybackProxyUrl(url));
    }

    [Theory]
    [InlineData("https://node1.whyknot.dev/api/proxy/manifest.mpd?q=abc", "mpd")]
    [InlineData("https://node1.whyknot.dev/api/proxy?q=legacy", "m3u8")]
    [InlineData("https://node1.whyknot.dev/api/popcorn/proxy/manifest.m3u8?clientId=c&index=1", "m3u8")]
    [InlineData("https://cdn.example.com/video/seg.ts?sig=abc", "ts")]
    [InlineData("https://node1.whyknot.dev/api/proxy?url=legacy", "bin")]
    [InlineData("https://cdn.example.com/video/no-extension?sig=abc", "")]
    [InlineData("not a url", "")]
    public void PlaybackProxyExtensionForTrustGateway_KeepsAvproDispatchExtension(
        string url,
        string expected)
    {
        Assert.Equal(expected, WhyKnotUrlPolicy.PlaybackProxyExtensionForTrustGateway(url));
    }

    [Fact]
    public void TrustGatewayUrlBuilder_WrapsOnlyWhyKnotPlaybackProxyTargets()
    {
        const string target = "https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=abc";
        bool ok = TrustGatewayUrlBuilder.TryBuild(
            51234,
            target,
            "test-session",
            out string localUrl);

        Assert.True(ok);
        Assert.StartsWith(
            "http://localhost.youtube.com:51234/play/testsession/manifest.m3u8?target=",
            localUrl);
        Assert.True(TrustGatewayUrlBuilder.TryExtractTarget(localUrl, out string extracted));
        Assert.Equal(target, extracted);

        Assert.False(TrustGatewayUrlBuilder.TryBuild(
            51234,
            "https://cdn.example.com/video/seg.ts",
            "test-session",
            out _));

        Assert.False(TrustGatewayUrlBuilder.TryBuild(
            51234,
            "http://localhost.youtube.com:51234/play/a/manifest.m3u8?target=x",
            "test-session",
            out _));

        Assert.True(TrustGatewayUrlBuilder.TryBuild(
            51234,
            "https://node1.whyknot.dev/api/proxy/manifest.m3u8?note=localhost.youtube.com",
            "test-session",
            out _));
    }

    [Fact]
    public void TrustGatewayUrlBuilder_ExtractTargetRejectsNonGatewayUrls()
    {
        Assert.False(TrustGatewayUrlBuilder.TryExtractTarget(
            "https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=abc",
            out _));
        Assert.False(TrustGatewayUrlBuilder.TryExtractTarget(
            "http://localhost.youtube.com:51234/play/a/manifest.m3u8?target=not-base64",
            out _));
    }
}
