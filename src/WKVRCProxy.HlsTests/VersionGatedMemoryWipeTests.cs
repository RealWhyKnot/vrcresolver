using System;
using System.IO;
using System.Text.Json;
using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

// The memory is wiped on any app-version change. Rationale: learned rankings are cheap to rebuild
// (~one cascade per host) but keeping stale state across a behavior-changing update is expensive —
// e.g. a "success" written by pre-playback-feedback logic would survive forever and keep the
// fast-path locked on a broken strategy. Each new binary starts from a clean slate.
public class VersionGatedMemoryWipeTests : IDisposable
{
    private readonly string _tmpDir;

    public VersionGatedMemoryWipeTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "wkvrc-version-wipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    private void WriteVersion(string v) => File.WriteAllText(Path.Combine(_tmpDir, "version.txt"), v);

    [Fact]
    public void LoadSave_Preserves_WhenVersionMatches()
    {
        WriteVersion("1.0.0");
        var m1 = new StrategyMemory(null, _tmpDir);
        m1.RecordSuccess("example.com:vod", "tier1:plain");
        m1.Flush();

        var m2 = new StrategyMemory(null, _tmpDir);
        m2.Load();
        Assert.NotNull(m2.GetPreferred("example.com:vod"));
    }

    [Fact]
    public void Load_WipesMemory_WhenVersionChanges()
    {
        WriteVersion("1.0.0");
        var m1 = new StrategyMemory(null, _tmpDir);
        m1.RecordSuccess("example.com:vod", "tier1:plain");
        m1.Flush();

        // Simulate an app update.
        WriteVersion("1.0.1");
        var m2 = new StrategyMemory(null, _tmpDir);
        m2.Load();
        Assert.Null(m2.GetPreferred("example.com:vod"));
        Assert.Equal(0, m2.EntryCount);
    }

    [Fact]
    public void Load_WipesLegacyUnversionedFile()
    {
        // Pre-envelope format: bare dictionary at the top level.
        WriteVersion("2.0.0");
        var legacy = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<StrategyMemoryEntry>>
        {
            ["example.com:vod"] = new() { new StrategyMemoryEntry { StrategyName = "tier2:cloud-whyknot", SuccessCount = 28, LastSuccess = DateTime.UtcNow } }
        };
        File.WriteAllText(Path.Combine(_tmpDir, "strategy_memory.json"),
            JsonSerializer.Serialize(legacy, new JsonSerializerOptions { WriteIndented = true }));

        var m = new StrategyMemory(null, _tmpDir);
        m.Load();
        Assert.Null(m.GetPreferred("example.com:vod"));
    }

    [Fact]
    public void Load_NoVersionFile_DoesNotWipe_ForDevRuns()
    {
        // `dotnet run` from source has no version.txt — wiping on every compile would be
        // pathological for iteration. Dev builds keep their memory.
        var m1 = new StrategyMemory(null, _tmpDir);
        m1.RecordSuccess("example.com:vod", "tier1:plain");
        m1.Flush();

        var m2 = new StrategyMemory(null, _tmpDir);
        m2.Load();
        Assert.NotNull(m2.GetPreferred("example.com:vod"));
    }
}
