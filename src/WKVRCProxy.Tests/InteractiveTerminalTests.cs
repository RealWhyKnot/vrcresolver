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
            EmptyBandwidth(),
            DateTime.UtcNow,
            meshConnected: false,
            spinnerIndex: 0,
            width: 120,
            input: "settings");

        Assert.Contains("VRChat waiting 0 B served", line);
        Assert.Contains("WhyKnot idle", line);
        Assert.Contains("reconnecting", line);
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
            new WatchdogBandwidthSnapshot(
                CurrentBytesPerSecond: 4096,
                PeakBytesPerSecond: 4096,
                HistoryBytesPerSecond: new long[] { 0, 1024, 4096 }),
            now,
            meshConnected: true,
            spinnerIndex: 1,
            width: 120,
            input: "");

        Assert.Contains("VRChat", line);
        Assert.Contains("serving 4.0 KB/s now", line);
        Assert.Contains("WhyKnot", line);
        Assert.Contains("pulling", line);
        Assert.Contains("online", line);
    }

    [Fact]
    public void Format_ReturnsStyledRunsWhenActivityIsActive()
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

        TerminalFrame frame = TerminalStatusFormatter.Format(
            snapshot,
            new WatchdogBandwidthSnapshot(4096, 4096, new long[] { 0, 1024, 4096 }),
            now,
            meshConnected: true,
            spinnerIndex: 2,
            width: 120,
            input: "",
            animationsEnabled: true,
            unicodeSymbols: true);

        Assert.True(frame.Runs.Count > 1);
        Assert.Contains(frame.Runs, run => run.Text == "serving" && run.Color != ConsoleColor.DarkGray);
        Assert.Contains(frame.Runs, run => run.Text == "pulling" && run.Color != ConsoleColor.DarkGray);
    }

    [Fact]
    public void FormatLine_IsStableWhenAnimationsAreDisabled()
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

        string first = TerminalStatusFormatter.FormatLine(
            snapshot,
            new WatchdogBandwidthSnapshot(4096, 4096, new long[] { 0, 1024, 4096 }),
            now,
            meshConnected: true,
            spinnerIndex: 0,
            width: 120,
            input: "",
            animationsEnabled: false,
            unicodeSymbols: false);
        string second = TerminalStatusFormatter.FormatLine(
            snapshot,
            new WatchdogBandwidthSnapshot(4096, 4096, new long[] { 0, 1024, 4096 }),
            now,
            meshConnected: true,
            spinnerIndex: 3,
            width: 120,
            input: "",
            animationsEnabled: false,
            unicodeSymbols: false);

        Assert.Equal(first, second);
        Assert.Contains("VRChat * serving", first);
        Assert.Contains("WhyKnot * pulling", first);
    }

    [Fact]
    public void FormatLine_FitsNarrowConsoleWidth()
    {
        string line = TerminalStatusFormatter.FormatLine(
            EmptySnapshot(),
            EmptyBandwidth(),
            DateTime.UtcNow,
            meshConnected: true,
            spinnerIndex: 0,
            width: 42,
            input: new string('x', 100));

        Assert.True(line.Length <= 42);
        Assert.Contains("wkvrc>", line);
    }

    [Fact]
    public void Renderer_UsesFastRefreshOnlyDuringAnimatedActivity()
    {
        var now = DateTime.UtcNow;
        var renderer = new TerminalRenderer(
            snapshot: () => new WatchdogActivitySnapshot(
                ResolvesTotal: 0,
                ResolvesViaLhYt: 0,
                ResolvesCacheHits: 0,
                BytesEstimateTotal: 0,
                ReconnectCount: 0,
                RelayRequestsTotal: 1,
                RelayBytesTotal: 1,
                WhyKnotRelayRequestsTotal: 0,
                WhyKnotRelayBytesTotal: 0,
                LastRelayUtc: now,
                LastWhyKnotRelayUtc: null),
            bandwidth: EmptyBandwidth,
            meshConnected: () => true,
            spinnerIndex: () => 0,
            input: () => "",
            settings: () => new AppSettings().Normalize(),
            animationsAvailable: () => true);

        Assert.True(renderer.ShouldUseFastRefresh());
    }

    [Fact]
    public void Renderer_UsesIdleRefreshWhenNothingIsMoving()
    {
        var renderer = new TerminalRenderer(
            snapshot: EmptySnapshot,
            bandwidth: EmptyBandwidth,
            meshConnected: () => true,
            spinnerIndex: () => 0,
            input: () => "",
            settings: () => new AppSettings().Normalize(),
            animationsAvailable: () => true);

        Assert.False(renderer.ShouldUseFastRefresh());
    }

    [Fact]
    public void Renderer_UsesIdleRefreshWhenAnimationsAreDisabled()
    {
        var now = DateTime.UtcNow;
        var settings = new AppSettings().Normalize();
        settings.Terminal.Animations = false;
        var renderer = new TerminalRenderer(
            snapshot: () => new WatchdogActivitySnapshot(
                ResolvesTotal: 0,
                ResolvesViaLhYt: 0,
                ResolvesCacheHits: 0,
                BytesEstimateTotal: 0,
                ReconnectCount: 0,
                RelayRequestsTotal: 1,
                RelayBytesTotal: 1,
                WhyKnotRelayRequestsTotal: 0,
                WhyKnotRelayBytesTotal: 0,
                LastRelayUtc: now,
                LastWhyKnotRelayUtc: null),
            bandwidth: EmptyBandwidth,
            meshConnected: () => true,
            spinnerIndex: () => 0,
            input: () => "",
            settings: () => settings,
            animationsAvailable: () => true);

        Assert.False(renderer.ShouldUseFastRefresh());
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

    private static WatchdogBandwidthSnapshot EmptyBandwidth()
    {
        return new WatchdogBandwidthSnapshot(
            CurrentBytesPerSecond: 0,
            PeakBytesPerSecond: 0,
            HistoryBytesPerSecond: Array.Empty<long>());
    }
}
