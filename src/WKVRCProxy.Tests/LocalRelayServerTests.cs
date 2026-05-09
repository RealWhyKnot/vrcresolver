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
        string url = "https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=AbC-_dEf=&extra=hi%20there";
        string encoded = LocalRelayTargetResolver.EncodeTargetParam(url);

        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded);
        Assert.DoesNotContain("=", encoded);
        Assert.DoesNotContain(" ", encoded);

        string b64 = encoded.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }

        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Equal(url, decoded);
    }

    [Theory]
    [InlineData("/play/7f8a9b0c1d2e/manifest.m3u8", "/play/7f8a9b0c1d2e/")]
    [InlineData("/play/7f8a9b0c1d2e/sub/manifest.m3u8", "/play/7f8a9b0c1d2e/sub/")]
    [InlineData("/play/manifest.m3u8", "/play/")]
    [InlineData("/play", "/play/")]
    [InlineData("/play/", "/play/")]
    public void LocalPrefixForPath_ReturnsDirectoryPrefix(string path, string expected)
    {
        Assert.Equal(expected, LocalRelayTargetResolver.LocalPrefixForPath(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/playlist/manifest.m3u8")]
    [InlineData("/playback/manifest.m3u8")]
    public void LocalPrefixForPath_NonPlayPaths_FallBackToPlayRoot(string path)
    {
        Assert.Equal("/play/", LocalRelayTargetResolver.LocalPrefixForPath(path));
    }

    [Fact]
    public void TryResolveRelativeTarget_MapsNamespacedSegmentToUpstreamBase()
    {
        bool ok = LocalRelayTargetResolver.TryResolveRelativeTarget(
            "/play/7f8a9b0c1d2e/seg_000001.ts",
            "?range=bytes%3D0-",
            "/play/7f8a9b0c1d2e/",
            "https://node1.whyknot.dev/api/proxy/lazy-hls/wk_abc/",
            out string targetUrl);

        Assert.True(ok);
        Assert.Equal(
            "https://node1.whyknot.dev/api/proxy/lazy-hls/wk_abc/seg_000001.ts?range=bytes%3D0-",
            targetUrl);
    }

    [Fact]
    public void TryResolveRelativeTarget_UsesDirectoryOfManifestUrl()
    {
        string manifestUrl = "https://node1.whyknot.dev/api/proxy/lazy-hls/wk_abc/index.m3u8?token=abc";
        string upstreamBase = new Uri(new Uri(manifestUrl), ".").ToString();

        bool ok = LocalRelayTargetResolver.TryResolveRelativeTarget(
            "/play/7f8a9b0c1d2e/seg_000002.ts",
            "",
            "/play/7f8a9b0c1d2e/",
            upstreamBase,
            out string targetUrl);

        Assert.True(ok);
        Assert.Equal(
            "https://node1.whyknot.dev/api/proxy/lazy-hls/wk_abc/seg_000002.ts",
            targetUrl);
    }

    [Fact]
    public void TryResolveRelativeTarget_RejectsPathOutsideNamespace()
    {
        bool ok = LocalRelayTargetResolver.TryResolveRelativeTarget(
            "/play/other/seg_000001.ts",
            "",
            "/play/7f8a9b0c1d2e/",
            "https://node1.whyknot.dev/api/proxy/lazy-hls/wk_abc/",
            out string targetUrl);

        Assert.False(ok);
        Assert.Equal("", targetUrl);
    }

    [Fact]
    public void TryResolveRelativeTarget_RejectsNamespaceRootFetch()
    {
        bool ok = LocalRelayTargetResolver.TryResolveRelativeTarget(
            "/play/7f8a9b0c1d2e/",
            "",
            "/play/7f8a9b0c1d2e/",
            "https://node1.whyknot.dev/api/proxy/lazy-hls/wk_abc/",
            out string targetUrl);

        Assert.False(ok);
        Assert.Equal("", targetUrl);
    }

    [Fact]
    public void TryResolve_TargetParamRegistersRelativeNamespace()
    {
        var resolver = new LocalRelayTargetResolver();
        string manifestUrl = "https://node1.whyknot.dev/api/proxy/lazy-hls/wk_abc/index.m3u8";
        string targetParam = LocalRelayTargetResolver.EncodeTargetParam(manifestUrl);

        bool manifestOk = resolver.TryResolve(
            "/play/7f8a9b0c1d2e/manifest.m3u8",
            "?target=" + targetParam,
            targetParam,
            out LocalRelayTarget manifestTarget);

        bool segmentOk = resolver.TryResolve(
            "/play/7f8a9b0c1d2e/seg_000003.ts",
            "",
            null,
            out LocalRelayTarget segmentTarget);

        Assert.True(manifestOk);
        Assert.Equal("target", manifestTarget.Kind);
        Assert.Equal(manifestUrl, manifestTarget.Url);

        Assert.True(segmentOk);
        Assert.Equal("relative", segmentTarget.Kind);
        Assert.Equal(
            "https://node1.whyknot.dev/api/proxy/lazy-hls/wk_abc/seg_000003.ts",
            segmentTarget.Url);
    }
}
