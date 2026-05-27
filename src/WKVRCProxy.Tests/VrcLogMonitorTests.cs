using System.Runtime.Versioning;
using WKVRCProxy;
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
}
