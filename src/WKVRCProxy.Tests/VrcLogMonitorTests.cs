using System.Runtime.Versioning;
using WKVRCProxy;
using WKVRCProxy.Shared;
using Xunit;

namespace WKVRCProxy.Tests;

[SupportedOSPlatform("windows")]
public sealed class VrcLogMonitorTests
{
    [Theory]
    [InlineData(true, 1234, 1234)]
    [InlineData(false, 1234, 0)]
    public void InitialReadOffsetForNewFile_TailsOnlyFirstFile(bool firstFile, long fileLength, long expected)
    {
        Assert.Equal(expected, VrcLogMonitor.InitialReadOffsetForNewFile(fileLength, firstFile));
    }

    [Theory]
    [InlineData("[Always] [Video Playback] Switched to 1920x1080", 1920, 1080)]
    [InlineData("[AVProVideo] PostStateChanged: OpeningToPlaying fwidth=1280 fheight=720", 1280, 720)]
    public void TryParseObservedResolution_extracts_width_and_height(string line, int expectedWidth, int expectedHeight)
    {
        Assert.True(VrcLogMonitor.TryParseObservedResolution(line, out int width, out int height));
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void ProcessNewContent_stores_resolution_for_current_url()
    {
        using var monitor = new VrcLogMonitor(new MeshClient());
        const string url = "https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=abc";

        monitor.ProcessNewContent("[AVProVideo] Opening " + url + "\n");
        monitor.ProcessNewContent("[Always] [Video Playback] Switched to 1920x1080\n");

        Assert.Equal(1080, monitor.GetObservedDeliveredHeightForTests(url));
    }

    [Fact]
    public void ProcessNewContent_load_failure_arms_og_hint_before_evicting_cache()
    {
        using var temp = new TempDir();
        var cache = new ResolveCache(temp.ResolveCachePath);
        var hint = new OgFallbackHint();
        using var monitor = new VrcLogMonitor(new MeshClient(), cache, hint);
        const string sourceUrl = "https://virtualfilm.institute/watch?v=abc";
        const string playbackUrl = "https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=abc";

        cache.Store("node1.whyknot.dev", sourceUrl, "avpro", null, MakeResolved(playbackUrl));

        monitor.ProcessNewContent(
            "[AVProVideo] Opening " + playbackUrl + "\n"
            + "[AVProVideo] Error: Loading failed\n");

        Assert.True(hint.ShouldPreferOg(sourceUrl));
        Assert.False(cache.TryGetSourceUrlForResolved(playbackUrl, out _));
    }

    [Fact]
    public void MarkPlaybackFailure_arms_og_hint_for_silent_stall_path()
    {
        using var temp = new TempDir();
        var cache = new ResolveCache(temp.ResolveCachePath);
        var hint = new OgFallbackHint();
        using var monitor = new VrcLogMonitor(new MeshClient(), cache, hint);
        const string sourceUrl = "https://virtualfilm.institute/watch?v=abc";
        const string playbackUrl = "https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=abc";

        cache.Store("node1.whyknot.dev", sourceUrl, "avpro", null, MakeResolved(playbackUrl));

        var recovery = monitor.MarkPlaybackFailureForTests(playbackUrl);

        Assert.Equal(1, recovery.Evicted);
        Assert.True(recovery.OgHintArmed);
        Assert.True(hint.ShouldPreferOg(sourceUrl));
        Assert.False(cache.TryGetSourceUrlForResolved(playbackUrl, out _));
    }

    private static ResolveResponse MakeResolved(string playbackUrl)
    {
        return new ResolveResponse
        {
            Action = WireConstants.ActionResolved,
            Id = "ignored-on-store",
            Url = playbackUrl,
            Engine = "yt-dlp:no-cookies-default",
            Container = "mp4",
            VideoCodec = "h264",
            AudioCodec = "aac",
            Protocol = "https",
            AudioChannels = 2,
            ExpiresAt = DateTime.UtcNow.AddHours(1).ToString("o"),
        };
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string _path;

        public TempDir()
        {
            _path = Path.Combine(Path.GetTempPath(), "wkvrcproxy-tests-vrclog-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_path);
            ResolveCachePath = Path.Combine(_path, "resolve_cache.json");
        }

        public string ResolveCachePath { get; }

        public void Dispose()
        {
            try { Directory.Delete(_path, recursive: true); } catch { }
        }
    }
}
