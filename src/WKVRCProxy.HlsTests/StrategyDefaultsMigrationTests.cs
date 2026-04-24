using System.Collections.Generic;
using WKVRCProxy.Core.Models;
using Xunit;

namespace WKVRCProxy.HlsTests;

// The migration path is load-bearing: it's what lets users upgrading from an older WKVRCProxy
// build pick up the new default strategy priority list without losing their customizations.
// A regression here silently pins half the user base to a stale default ordering, so every
// branch gets coverage.
public class StrategyDefaultsMigrationTests
{
    [Fact]
    public void V1_DefaultList_MigratesToV2()
    {
        var saved = new List<string>(StrategyDefaults.PriorityDefaultsV1);
        bool migrated = StrategyDefaults.TryMigratePriorityList(saved, savedVersion: 1, out var result);

        Assert.True(migrated);
        Assert.Equal(StrategyDefaults.PriorityDefaultsV2, result);
    }

    [Fact]
    public void V2_DefaultList_StaysUnchanged()
    {
        var saved = new List<string>(StrategyDefaults.PriorityDefaultsV2);
        bool migrated = StrategyDefaults.TryMigratePriorityList(saved, savedVersion: StrategyDefaults.CurrentVersion, out var result);

        Assert.False(migrated);
        Assert.Equal(StrategyDefaults.PriorityDefaultsV2, result);
    }

    [Fact]
    public void CustomizedList_IsPreserved_EvenOnOldVersion()
    {
        // User added a new strategy at the top, removed another; not exactly any known default.
        var customized = new List<string>
        {
            "tier2:cloud-whyknot",     // user promoted cloud to position 0
            "tier1:yt-combo",
            "tier1:ipv6",
            "tier1:browser-extract",   // user demoted to just above the tail
        };
        bool migrated = StrategyDefaults.TryMigratePriorityList(customized, savedVersion: 1, out var result);

        Assert.False(migrated);
        Assert.Equal(customized, result);
    }

    [Fact]
    public void NullSaved_ReturnsV2Defaults_WithoutMigrationFlag()
    {
        bool migrated = StrategyDefaults.TryMigratePriorityList(null, savedVersion: 0, out var result);

        Assert.False(migrated);
        Assert.Equal(StrategyDefaults.PriorityDefaultsV2, result);
    }

    [Fact]
    public void EmptyList_IsTreatedAsCustomized()
    {
        // An empty list is a legitimate user choice (disable everything in priority, fall back to
        // built-in priority numbers). Migration should NOT replace it.
        var saved = new List<string>();
        bool migrated = StrategyDefaults.TryMigratePriorityList(saved, savedVersion: 1, out var result);

        Assert.False(migrated);
        Assert.Empty(result);
    }

    [Fact]
    public void YouTubeComboClientOrderDefault_MatchesAllKnownClients()
    {
        // Regression guard: the combo default must include every client the yt-dlp YouTube
        // extractor currently accepts, in a sensible order. If we add/remove one here, this test
        // doc-comments what changed in code review.
        Assert.Contains("tv_simply", StrategyDefaults.YouTubeComboClientOrderDefault);
        Assert.Contains("web_safari", StrategyDefaults.YouTubeComboClientOrderDefault);
        Assert.Contains("mweb", StrategyDefaults.YouTubeComboClientOrderDefault);
        Assert.Contains("ios", StrategyDefaults.YouTubeComboClientOrderDefault);
        Assert.Contains("android", StrategyDefaults.YouTubeComboClientOrderDefault);
        // Order assertion: TV family first (currently most bot-resistant).
        Assert.Equal("tv_simply", StrategyDefaults.YouTubeComboClientOrderDefault[0]);
    }
}
