using System.Runtime.Versioning;
using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

[SupportedOSPlatform("windows")]
public class InteractiveTerminalTests
{
    [Fact]
    public void FormatLine_ShowsInactiveActivityWithStablePrompt()
    {
        string line = TerminalStatusFormatter.FormatLine(
            EmptySnapshot(),
            DateTime.UtcNow,
            meshConnected: false,
            spinnerIndex: 0,
            width: 120,
            input: "settings");

        Assert.Contains("lh-yt - 0 B", line);
        Assert.Contains("whyknot - 0 B", line);
        Assert.Contains("mesh down", line);
        Assert.EndsWith("wkvrc> settings", line);
    }

    [Fact]
    public void FormatLine_AnimatesRecentRelayAndWhyKnotActivity()
    {
        var now = DateTime.UtcNow;
        var snapshot = new WatchdogActivitySnapshot(
            ResolvesTotal: 0,
            ResolvesViaLhYt: 0,
            ResolvesCacheHits: 0,
            BytesEstimateTotal: 0,
            ReconnectCount: 0,
            RelayRequestsTotal: 1,
            RelayBytesTotal: 2048,
            WhyKnotRelayRequestsTotal: 1,
            WhyKnotRelayBytesTotal: 1024,
            LastRelayUtc: now,
            LastWhyKnotRelayUtc: now);

        string line = TerminalStatusFormatter.FormatLine(
            snapshot,
            now,
            meshConnected: true,
            spinnerIndex: 1,
            width: 120,
            input: "");

        Assert.Contains("lh-yt / 2.0 KB", line);
        Assert.Contains("whyknot / 1.0 KB", line);
        Assert.Contains("mesh up", line);
    }

    [Fact]
    public void FormatLine_FitsNarrowConsoleWidth()
    {
        string line = TerminalStatusFormatter.FormatLine(
            EmptySnapshot(),
            DateTime.UtcNow,
            meshConnected: true,
            spinnerIndex: 0,
            width: 42,
            input: new string('x', 100));

        Assert.True(line.Length <= 42);
        Assert.Contains("wkvrc>", line);
    }

    private static WatchdogActivitySnapshot EmptySnapshot()
    {
        return new WatchdogActivitySnapshot(
            ResolvesTotal: 0,
            ResolvesViaLhYt: 0,
            ResolvesCacheHits: 0,
            BytesEstimateTotal: 0,
            ReconnectCount: 0,
            RelayRequestsTotal: 0,
            RelayBytesTotal: 0,
            WhyKnotRelayRequestsTotal: 0,
            WhyKnotRelayBytesTotal: 0,
            LastRelayUtc: null,
            LastWhyKnotRelayUtc: null);
    }
}
