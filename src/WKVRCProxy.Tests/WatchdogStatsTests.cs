using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

public class WatchdogStatsTests
{
    [Fact]
    public void RecordRelayBytes_CountsWhyKnotTargetsSeparately()
    {
        WatchdogStats.ResetForTests();

        WatchdogStats.RecordRelayRequest("https://node1.whyknot.dev/api/proxy/manifest.m3u8?q=abc");
        WatchdogStats.RecordRelayBytes("https://node1.whyknot.dev/api/proxy/segment.ts?q=abc", 100);
        WatchdogStats.RecordRelayBytes("https://example.com/video.ts", 25);

        WatchdogActivitySnapshot snapshot = WatchdogStats.GetActivitySnapshot();

        Assert.Equal(1, snapshot.RelayRequestsTotal);
        Assert.Equal(125, snapshot.RelayBytesTotal);
        Assert.Equal(1, snapshot.WhyKnotRelayRequestsTotal);
        Assert.Equal(100, snapshot.WhyKnotRelayBytesTotal);
        Assert.True(snapshot.LastRelayUtc.HasValue);
        Assert.True(snapshot.LastWhyKnotRelayUtc.HasValue);
    }

    [Fact]
    public void ActivitySnapshot_ReportsRecentRelayActivity()
    {
        var now = DateTime.UtcNow;
        var snapshot = new WatchdogActivitySnapshot(
            ResolvesTotal: 0,
            ResolvesViaLhYt: 0,
            ResolvesCacheHits: 0,
            BytesEstimateTotal: 0,
            ReconnectCount: 0,
            RelayRequestsTotal: 1,
            RelayBytesTotal: 50,
            WhyKnotRelayRequestsTotal: 1,
            WhyKnotRelayBytesTotal: 50,
            LastRelayUtc: now - TimeSpan.FromMilliseconds(100),
            LastWhyKnotRelayUtc: now - TimeSpan.FromMilliseconds(100));

        Assert.True(snapshot.RelayActive(now, TimeSpan.FromSeconds(1)));
        Assert.True(snapshot.WhyKnotActive(now, TimeSpan.FromSeconds(1)));
        Assert.False(snapshot.RelayActive(now + TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void BandwidthSnapshot_ReportsRecentBytesBySecond()
    {
        WatchdogStats.ResetForTests();
        var now = new DateTime(2026, 5, 10, 12, 0, 10, DateTimeKind.Utc);

        WatchdogStats.RecordRelayBytesAt("https://node1.whyknot.dev/api/proxy/a.ts", 1000, now - TimeSpan.FromSeconds(2));
        WatchdogStats.RecordRelayBytesAt("https://node1.whyknot.dev/api/proxy/b.ts", 2000, now);
        WatchdogStats.RecordRelayBytesAt("https://node1.whyknot.dev/api/proxy/c.ts", 3000, now);

        WatchdogBandwidthSnapshot bandwidth = WatchdogStats.GetBandwidthSnapshot(now, seconds: 4);

        Assert.Equal(new long[] { 0, 1000, 0, 5000 }, bandwidth.HistoryBytesPerSecond);
        Assert.Equal(5000, bandwidth.CurrentBytesPerSecond);
        Assert.Equal(5000, bandwidth.PeakBytesPerSecond);
        Assert.True(bandwidth.HasTraffic);
    }
}
