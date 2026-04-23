using System;
using System.IO;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

// Regression tests for the playback-feedback loop added after the "tier2:cloud-whyknot 28W/0L"
// dead-end bug: resolution kept reporting success but AVPro silently rejected every URL because
// the untrusted host wasn't relay-wrapped. The fix records PlaybackFailed → demote-after-one.
public class PlaybackFailedDemotionTests : IDisposable
{
    private readonly string _tmpDir;

    public PlaybackFailedDemotionTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "wkvrc-playback-failed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public void PlaybackFailed_DemotesAfterSingleFailure()
    {
        var m = new StrategyMemory(null, _tmpDir);
        // Build up a healthy streak first (simulating the old bug where 28 wins accumulated).
        for (int i = 0; i < 28; i++)
            m.RecordSuccess("youtube.com:vod", "tier2:cloud-whyknot");

        Assert.Equal("tier2:cloud-whyknot", m.GetPreferred("youtube.com:vod")!.StrategyName);

        // One playback failure must demote — this is the "unkillable resolution" contract.
        m.RecordFailure("youtube.com:vod", "tier2:cloud-whyknot", StrategyFailureKind.PlaybackFailed);

        Assert.Null(m.GetPreferred("youtube.com:vod"));
    }

    [Fact]
    public void PlaybackFailed_ThresholdIsOne()
    {
        Assert.Equal(1, StrategyMemory.DemoteThresholdFor(StrategyFailureKind.PlaybackFailed));
    }

    [Fact]
    public void Timeout_DoesNotDemoteOnFirstFailure()
    {
        // Sanity-check that PlaybackFailed's one-strike rule hasn't accidentally affected other kinds.
        var m = new StrategyMemory(null, _tmpDir);
        m.RecordSuccess("foo.com:vod", "tier1:plain");
        m.RecordFailure("foo.com:vod", "tier1:plain", StrategyFailureKind.Timeout);
        Assert.NotNull(m.GetPreferred("foo.com:vod"));
    }
}
