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

    [Theory]
    [InlineData("localhost.youtube.com:51234", 51234, true)]
    [InlineData("LOCALHOST.YOUTUBE.COM:51234", 51234, true)]
    [InlineData("127.0.0.1:51234", 51234, true)]
    [InlineData("localhost:51234", 51234, true)]
    [InlineData("node1.whyknot.dev:51234", 51234, false)]
    [InlineData("localhost.youtube.com:51235", 51234, false)]
    [InlineData("", 51234, false)]
    public void Security_AllowsOnlyExpectedLocalHosts(string host, int port, bool expected)
    {
        Assert.Equal(expected, LocalRelaySecurity.IsAllowedHostHeader(host, port));
    }

    [Theory]
    [InlineData("https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=abc", true)]
    [InlineData("https://whyknot.dev/api/proxy/lazy-hls/wk_abc/index.m3u8", true)]
    [InlineData("https://node1.whyknot.dev/api/popcorn/proxy/manifest.m3u8?clientId=c&index=1", true)]
    [InlineData("https://node1.whyknot.dev/api/restream/shared-status", false)]
    [InlineData("https://example.com/api/proxy/manifest.m3u8?q=abc", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("not a url", false)]
    public void Security_AllowsOnlyWhyKnotProxyTargets(string targetUrl, bool expected)
    {
        Assert.Equal(expected, LocalRelaySecurity.IsAllowedTargetUrl(targetUrl, out _));
    }

    [Fact]
    public void ManifestLocalizer_RewritesFirstPartyPopcornProxyUrls()
    {
        string upstream = "https://node2.whyknot.dev/api/popcorn/proxy/seg.ts?clientId=c&url=abc";
        string manifest = "#EXTM3U\n#EXTINF:4.000,\n" + upstream + "\n";

        string localized = LocalRelayManifestLocalizer.Localize(
            manifest,
            "/play/7f8a9b0c1d2e/manifest.m3u8");

        Assert.DoesNotContain(upstream, localized);
        Assert.Contains("proxy/", localized);
        Assert.Contains("/seg.ts?target=", localized);
    }

    [Fact]
    public void ManifestLocalizer_RewritesFirstPartyProxyLinesToLocalTargets()
    {
        string upstream = "https://node1.whyknot.dev/api/proxy/lazy-hls/wk_abc/index.m3u8";
        string manifest = "#EXTM3U\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=1000000\n"
            + upstream + "\n";

        string localized = LocalRelayManifestLocalizer.Localize(
            manifest,
            "/play/7f8a9b0c1d2e/manifest.m3u8");

        Assert.DoesNotContain("https://node1.whyknot.dev", localized);
        Assert.Contains("/index.m3u8?target=", localized);
        Assert.Contains("proxy/", localized);

        string targetParam = ExtractTargetParam(localized);
        var resolver = new LocalRelayTargetResolver();
        bool ok = resolver.TryResolve(
            "/play/7f8a9b0c1d2e/" + ExtractLocalPath(localized),
            "?target=" + targetParam,
            targetParam,
            out LocalRelayTarget target);

        Assert.True(ok);
        Assert.Equal(upstream, target.Url);
    }

    [Fact]
    public void ManifestLocalizer_RewritesFirstPartyQuotedUris()
    {
        string upstream = "https://node2.whyknot.dev/api/proxy/seg.ts?url=abc";
        string manifest = "#EXTM3U\n"
            + "#EXT-X-KEY:METHOD=AES-128,URI=\"" + upstream + "\"\n"
            + "#EXTINF:4.000,\n"
            + "seg_000001.ts\n";

        string localized = LocalRelayManifestLocalizer.Localize(
            manifest,
            "/play/7f8a9b0c1d2e/proxy/index.m3u8");

        Assert.DoesNotContain(upstream, localized);
        Assert.Contains("URI=\"proxy/", localized);
        Assert.Contains("/seg.ts?target=", localized);
        Assert.Contains("seg_000001.ts", localized);
    }

    [Fact]
    public void ManifestLocalizer_GivesSameFileNameTargetsDistinctLocalNamespaces()
    {
        string first = "https://node1.whyknot.dev/api/proxy/a/index.m3u8?x=1";
        string second = "https://node1.whyknot.dev/api/proxy/b/index.m3u8?x=2";
        string manifest = "#EXTM3U\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=1000000\n"
            + first + "\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=2000000\n"
            + second + "\n";

        string localized = LocalRelayManifestLocalizer.Localize(
            manifest,
            "/play/7f8a9b0c1d2e/manifest.m3u8");

        string[] localTargets = localized.Split('\n')
            .Where(line => line.StartsWith("proxy/", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, localTargets.Length);
        Assert.NotEqual(
            localTargets[0].Substring(0, localTargets[0].IndexOf("/index.m3u8", StringComparison.Ordinal)),
            localTargets[1].Substring(0, localTargets[1].IndexOf("/index.m3u8", StringComparison.Ordinal)));
    }

    [Fact]
    public void ManifestLocalizer_RewritesFirstPartyUrlsInsideXmlText()
    {
        string upstream = "https://node2.whyknot.dev/api/proxy/lazy-hls/wk_abc/seg_000001.m4s";
        string manifest = "<MPD><Period><BaseURL>" + upstream + "</BaseURL></Period></MPD>";

        string localized = LocalRelayManifestLocalizer.Localize(
            manifest,
            "/play/7f8a9b0c1d2e/manifest.mpd");

        Assert.DoesNotContain(upstream, localized);
        Assert.Contains("<BaseURL>proxy/", localized);
        Assert.Contains("/seg_000001.m4s?target=", localized);
    }

    [Fact]
    public void ManifestLocalizer_LeavesThirdPartyAbsoluteUrlsUntouched()
    {
        string upstream = "https://cdn.example.com/video/seg.ts";
        string manifest = "#EXTM3U\n#EXTINF:4.000,\n" + upstream;

        string localized = LocalRelayManifestLocalizer.Localize(
            manifest,
            "/play/7f8a9b0c1d2e/manifest.m3u8");

        Assert.Equal(manifest, localized);
    }

    [Theory]
    [InlineData("/play/a/manifest.m3u8", "https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=abc", "text/plain", true)]
    [InlineData("/play/a/manifest.bin", "https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=abc", "application/octet-stream", true)]
    [InlineData("/play/a/seg.ts", "https://node1.whyknot.dev/api/proxy/seg.ts?url=abc", "video/mp2t", false)]
    public void ManifestLocalizer_DetectsOnlyManifestShapes(
        string localPath,
        string targetUrl,
        string mediaType,
        bool expected)
    {
        var contentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        Assert.Equal(expected, LocalRelayManifestLocalizer.IsLikelyManifest(localPath, targetUrl, contentType));
    }

    private static string ExtractTargetParam(string localized)
    {
        const string marker = "?target=";
        int start = localized.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "localized manifest did not contain target param");
        start += marker.Length;
        int end = localized.IndexOfAny(new[] { '\r', '\n', '"' }, start);
        if (end < 0) end = localized.Length;
        return localized.Substring(start, end - start);
    }

    private static string ExtractLocalPath(string localized)
    {
        string[] lines = localized.Split('\n');
        string? local = lines.FirstOrDefault(line => line.StartsWith("proxy/", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(local), "localized manifest did not contain a local proxy path");
        int query = local.IndexOf('?');
        Assert.True(query > 0, "localized proxy path did not contain a query");
        return local.Substring(0, query);
    }
}
