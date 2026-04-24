using System;
using System.IO;
using System.Text.Json;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

public class StrategyMemoryTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly Logger? _logger = null; // null logger is supported for unit-test use.

    public StrategyMemoryTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "wkvrc-strategy-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public void KeyFor_NormalisesHostAndStripsWww()
    {
        // Legacy 2-arg overload — player is implicit "unknown". Kept for call sites that don't
        // know the player yet (migrations, external callers). Real resolution flow uses the
        // 3-arg overload (see KeyFor_IncludesPlayer).
        Assert.Equal("youtube.com:vod:unknown", StrategyMemory.KeyFor("https://www.YOUTUBE.com/watch?v=abc", false));
        Assert.Equal("vr-m.net:live:unknown", StrategyMemory.KeyFor("https://vr-m.net/p/9851.m3u8", true));
    }

    [Fact]
    public void KeyFor_IncludesPlayer()
    {
        // AVPro and Unity need different formats, so their fast-path memories must not collide.
        // The 3-arg KeyFor is the real production path — ResolutionEngine threads the player
        // through. Confirm the player segment is lowercased and slotted in as the third token.
        Assert.Equal("youtube.com:vod:avpro", StrategyMemory.KeyFor("https://www.youtube.com/watch?v=abc", false, "AVPro"));
        Assert.Equal("youtube.com:vod:unity", StrategyMemory.KeyFor("https://www.youtube.com/watch?v=abc", false, "Unity"));
        Assert.NotEqual(
            StrategyMemory.KeyFor("https://www.youtube.com/watch?v=abc", false, "AVPro"),
            StrategyMemory.KeyFor("https://www.youtube.com/watch?v=abc", false, "Unity"));
    }

    [Fact]
    public void RecordSuccess_IncrementsAndResetsFailureStreak()
    {
        var m = new StrategyMemory(_logger, _tmpDir);
        m.RecordFailure("foo.com:vod", "tier1:plain");
        m.RecordFailure("foo.com:vod", "tier1:plain");
        m.RecordSuccess("foo.com:vod", "tier1:plain");

        var best = m.GetPreferred("foo.com:vod");
        Assert.NotNull(best);
        Assert.Equal("tier1:plain", best!.StrategyName);
        Assert.Equal(1, best.SuccessCount);
        Assert.Equal(0, best.ConsecutiveFailures);
        Assert.Equal(1, best.FailureCount);
    }

    [Fact]
    public void Demotes_Strategy_After_Three_Consecutive_Failures()
    {
        var m = new StrategyMemory(_logger, _tmpDir);
        m.RecordSuccess("foo.com:vod", "tier1:default");
        m.RecordFailure("foo.com:vod", "tier1:default");
        m.RecordFailure("foo.com:vod", "tier1:default");
        m.RecordFailure("foo.com:vod", "tier1:default");

        // 3 consecutive failures → demoted. GetPreferred hides it.
        Assert.Null(m.GetPreferred("foo.com:vod"));
        // GetAll still shows it for diagnostics.
        var all = m.GetAll("foo.com:vod");
        Assert.Single(all);
        Assert.True(all[0].ConsecutiveFailures >= 3);
    }

    [Fact]
    public void Tier4_Successes_Are_Never_Recorded()
    {
        var m = new StrategyMemory(_logger, _tmpDir);
        m.RecordSuccess("foo.com:live", "tier4:passthrough");
        Assert.Null(m.GetPreferred("foo.com:live"));
    }

    [Fact]
    public void Prefers_Higher_NetScore_Between_Strategies()
    {
        var m = new StrategyMemory(_logger, _tmpDir);
        m.RecordSuccess("bar.com:vod", "tier1:default");   // 1W
        m.RecordSuccess("bar.com:vod", "tier1:default");   // 2W
        m.RecordSuccess("bar.com:vod", "tier1:vrchat-ua"); // 1W
        m.RecordFailure("bar.com:vod", "tier1:vrchat-ua"); // 1L — net 0

        var best = m.GetPreferred("bar.com:vod");
        Assert.NotNull(best);
        Assert.Equal("tier1:default", best!.StrategyName);
    }

    [Fact]
    public void Migrates_Legacy_TierMemory_Json()
    {
        string legacy = Path.Combine(_tmpDir, "tier_memory.json");
        var legacyPayload = new
        {
            youtube_com_vod = new { Tier = "tier1", SuccessCount = 5, LastSuccess = DateTime.UtcNow.AddDays(-1) }
        };
        // Hand-roll JSON so keys with ':' are preserved.
        File.WriteAllText(legacy, "{\n  \"youtube.com:vod\": { \"Tier\": \"tier1\", \"SuccessCount\": 5, \"LastSuccess\": \"" + DateTime.UtcNow.AddDays(-1).ToString("o") + "\" }\n}");

        var m = new StrategyMemory(_logger, _tmpDir);
        m.Load();

        var best = m.GetPreferred("youtube.com:vod");
        Assert.NotNull(best);
        Assert.Equal("tier1:po+impersonate", best!.StrategyName);
        Assert.Equal(5, best.SuccessCount);
    }

    [Fact]
    public void Load_From_Strategy_Memory_Json_Round_Trips()
    {
        var m1 = new StrategyMemory(_logger, _tmpDir);
        m1.RecordSuccess("quux.com:vod", "tier2:cloud-whyknot");
        m1.Save();

        var m2 = new StrategyMemory(_logger, _tmpDir);
        m2.Load();

        var best = m2.GetPreferred("quux.com:vod");
        Assert.NotNull(best);
        Assert.Equal("tier2:cloud-whyknot", best!.StrategyName);
        Assert.Equal(1, best.SuccessCount);
    }

    [Fact]
    public void ForgetKey_DropsEntry()
    {
        var m = new StrategyMemory(_logger, _tmpDir);
        m.RecordSuccess("baz.net:live", "tier1:default");
        Assert.NotNull(m.GetPreferred("baz.net:live"));
        m.ForgetKey("baz.net:live");
        Assert.Null(m.GetPreferred("baz.net:live"));
    }
}
