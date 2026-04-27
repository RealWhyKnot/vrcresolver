using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WKVRCProxy.Core;
using WKVRCProxy.Core.Models;
using Xunit;

namespace WKVRCProxy.HlsTests;

// Coverage for the override-tracking schema introduced when we retired the
// StrategyPriorityDefaultsVersion migration scheme. The new model: every default-tracked field
// is re-pulled from source on load unless its JSON key is in AppConfig.UserOverriddenKeys.
// Pre-override-tracking configs are auto-classified on first load by comparing values to frozen
// historical defaults.
//
// File name retained from the prior migration-test era for git blame continuity; this is the
// canonical home for default-sync regression tests.
public class StrategyDefaultsMigrationTests
{
    [Fact]
    public void PriorityDefaults_IncludesWarpVariants()
    {
        // The whole point of Phase C: warp+ entries are surfaced in the priority list so users
        // can see and reorder them in Settings.
        Assert.Contains("tier1:warp+default", StrategyDefaults.PriorityDefaults);
        Assert.Contains("tier1:warp+vrchat-ua", StrategyDefaults.PriorityDefaults);
    }

    [Fact]
    public void HistoricalDefaults_AreFrozen()
    {
        // V1 and V2 are inputs to legacy-override inference. If they ever change, configs that
        // were saved at those defaults stop being recognized → users get spurious "(custom)"
        // tags and stop receiving default updates. Pin the contents here.
        Assert.DoesNotContain("tier1:warp+default", StrategyDefaults.PriorityDefaultsV2);
        Assert.DoesNotContain("tier1:warp+vrchat-ua", StrategyDefaults.PriorityDefaultsV2);
        Assert.Equal("tier3:plain", StrategyDefaults.PriorityDefaultsV2.Last());
    }

    [Fact]
    public void MatchesAnyHistoricalPriorityDefault_RecognizesV1AndV2AndCurrent()
    {
        Assert.True(StrategyDefaults.MatchesAnyHistoricalPriorityDefault(
            new List<string>(StrategyDefaults.PriorityDefaultsV1)));
        Assert.True(StrategyDefaults.MatchesAnyHistoricalPriorityDefault(
            new List<string>(StrategyDefaults.PriorityDefaultsV2)));
        Assert.True(StrategyDefaults.MatchesAnyHistoricalPriorityDefault(
            new List<string>(StrategyDefaults.PriorityDefaults)));
    }

    [Fact]
    public void MatchesAnyHistoricalPriorityDefault_RejectsCustomizedList()
    {
        var custom = new List<string>(StrategyDefaults.PriorityDefaultsV2);
        custom.Reverse(); // Same elements, different order — still a customization.
        Assert.False(StrategyDefaults.MatchesAnyHistoricalPriorityDefault(custom));
        Assert.False(StrategyDefaults.MatchesAnyHistoricalPriorityDefault(new List<string>()));
    }

    [Fact]
    public void DefaultTrackedFields_CoversEveryListField()
    {
        // Sanity check: when a new default-tracked list field is added to AppConfig, both the
        // resetter and the legacy matcher must be wired or the override-tracking story falls
        // apart for that field. Failing assertion here is a reminder.
        var expectedKeys = new[] { "strategyPriority", "youtubeComboClientOrder", "nativeAvProUaHosts" };
        foreach (var key in expectedKeys)
        {
            Assert.True(DefaultTrackedFields.Resetters.ContainsKey(key),
                "Resetter missing for default-tracked field: " + key);
            Assert.True(DefaultTrackedFields.LegacyMatchers.ContainsKey(key),
                "LegacyMatcher missing for default-tracked field: " + key);
        }
    }

    [Fact]
    public void Resetter_StrategyPriority_RestoresCurrentDefault()
    {
        var cfg = new AppConfig { StrategyPriority = new List<string> { "tier1:plain" } };
        DefaultTrackedFields.Resetters["strategyPriority"](cfg);
        Assert.Equal(StrategyDefaults.PriorityDefaults, cfg.StrategyPriority);
    }

    [Fact]
    public void Resetter_YouTubeComboClientOrder_RestoresCurrentDefault()
    {
        var cfg = new AppConfig { YouTubeComboClientOrder = new List<string> { "ios" } };
        DefaultTrackedFields.Resetters["youtubeComboClientOrder"](cfg);
        Assert.Equal(StrategyDefaults.YouTubeComboClientOrderDefault, cfg.YouTubeComboClientOrder);
    }

    [Fact]
    public void Resetter_NativeAvProUaHosts_RestoresCurrentDefault()
    {
        var cfg = new AppConfig { NativeAvProUaHosts = new List<string>() };
        DefaultTrackedFields.Resetters["nativeAvProUaHosts"](cfg);
        Assert.Equal(new List<string> { "vr-m.net" }, cfg.NativeAvProUaHosts);
    }

    [Fact]
    public void LegacyMatcher_TreatsSavedV2DefaultAsNotOverridden()
    {
        var cfg = new AppConfig { StrategyPriority = new List<string>(StrategyDefaults.PriorityDefaultsV2) };
        Assert.True(DefaultTrackedFields.LegacyMatchers["strategyPriority"](cfg));
    }

    [Fact]
    public void LegacyMatcher_TreatsCustomizedListAsOverridden()
    {
        var cfg = new AppConfig { StrategyPriority = new List<string> { "tier1:plain", "tier3:plain" } };
        Assert.False(DefaultTrackedFields.LegacyMatchers["strategyPriority"](cfg));
    }

    [Fact]
    public void MaskIp_DefaultsToFalse()
    {
        Assert.False(new AppConfig().MaskIp);
    }

    [Fact]
    public void MaskIp_RoundTripsThroughSourceGenJson()
    {
        var cfg = new AppConfig { MaskIp = true };
        string json = JsonSerializer.Serialize(cfg, CoreJsonContext.Default.AppConfig);
        Assert.Contains("\"maskIp\": true", json);
        var parsed = JsonSerializer.Deserialize(json, CoreJsonContext.Default.AppConfig);
        Assert.NotNull(parsed);
        Assert.True(parsed!.MaskIp);
    }

    [Fact]
    public void SettingsManager_LegacyConfig_InfersOverridesAndPullsCurrentDefault()
    {
        // Pre-override-tracking config: StrategyPriority is a customized list (not any historical
        // default), userOverriddenKeys field is absent. After SettingsManager load + save + reload,
        // userOverriddenKeys should contain "strategyPriority" so the customized list survives the
        // next default-sync, and the file should now carry the field on disk.
        using var dir = new TempDir();
        var customList = new[] { "tier1:plain", "tier3:plain" };
        File.WriteAllText(Path.Combine(dir.Path, "app_config.json"),
            "{ \"strategyPriority\": [\"tier1:plain\", \"tier3:plain\"] }");

        var sm = new SettingsManager(dir.Path);
        Assert.Equal(customList, sm.Config.StrategyPriority);
        Assert.Contains("strategyPriority", sm.Config.UserOverriddenKeys, StringComparer.OrdinalIgnoreCase);

        // Persist + reload — override should still be honored, list still preserved.
        sm.Save();
        var reloaded = new SettingsManager(dir.Path);
        Assert.Equal(customList, reloaded.Config.StrategyPriority);
        Assert.Contains("strategyPriority", reloaded.Config.UserOverriddenKeys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsManager_ConfigAtCurrentDefault_PullsFutureDefaultUpdates()
    {
        // User is on the current default (no overrides). If we simulate a "newer" code default by
        // reloading after mutating the in-memory list to something stale, the next load should
        // re-pull the current default (since "strategyPriority" isn't in UserOverriddenKeys).
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "app_config.json"),
            "{ \"userOverriddenKeys\": [], \"strategyPriority\": [\"this-is-stale\"] }");

        var sm = new SettingsManager(dir.Path);
        // Not in overrides → sync should have pulled the current default.
        Assert.Equal(StrategyDefaults.PriorityDefaults, sm.Config.StrategyPriority);
        Assert.DoesNotContain("strategyPriority", sm.Config.UserOverriddenKeys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsManager_OverriddenKey_PreservesValueAcrossLoads()
    {
        // User has explicitly customized — the on-disk userOverriddenKeys protects their list.
        using var dir = new TempDir();
        var customList = new List<string> { "tier1:plain" };
        File.WriteAllText(Path.Combine(dir.Path, "app_config.json"),
            "{ \"userOverriddenKeys\": [\"strategyPriority\"], \"strategyPriority\": [\"tier1:plain\"] }");

        var sm = new SettingsManager(dir.Path);
        Assert.Equal(customList, sm.Config.StrategyPriority);
        Assert.Contains("strategyPriority", sm.Config.UserOverriddenKeys, StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; }
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "wkvrc-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
