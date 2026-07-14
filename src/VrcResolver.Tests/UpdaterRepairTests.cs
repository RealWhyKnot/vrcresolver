using VrcResolver;
using Xunit;

namespace VrcResolver.Tests;

public class UpdaterRepairTests : IDisposable
{
    private readonly string _root;

    public UpdaterRepairTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vrcresolver-tests-updater-repair-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ApplyIfPresent_replaces_stale_updater_with_staged_copy()
    {
        File.WriteAllText(Path.Combine(_root, "vrcresolver.Updater.exe"), "OLD");
        File.WriteAllText(Path.Combine(_root, "vrcresolver.Updater.next.exe"), "NEW");

        bool applied = UpdaterRepair.ApplyIfPresent(_root);

        Assert.True(applied);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(_root, "vrcresolver.Updater.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "vrcresolver.Updater.next.exe")));
        Assert.Empty(Directory.GetFiles(_root, "*.old-*"));
    }

    // Rename transition: the transitional release stages a launcher under
    // the pre-rename staged-updater name; the repair must swap it over the
    // stale pre-rename updater exe too.
    [Fact]
    public void ApplyIfPresent_also_applies_pre_rename_staged_pair()
    {
        File.WriteAllText(Path.Combine(_root, "WKVRCProxy.Updater.exe"), "OLD-REAL-UPDATER");
        File.WriteAllText(Path.Combine(_root, "WKVRCProxy.Updater.next.exe"), "LAUNCHER");

        bool applied = UpdaterRepair.ApplyIfPresent(_root);

        Assert.True(applied);
        Assert.Equal("LAUNCHER", File.ReadAllText(Path.Combine(_root, "WKVRCProxy.Updater.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "WKVRCProxy.Updater.next.exe")));
        Assert.Empty(Directory.GetFiles(_root, "*.old-*"));
    }

    [Fact]
    public void ApplyIfPresent_applies_both_pairs_in_one_pass()
    {
        File.WriteAllText(Path.Combine(_root, "vrcresolver.Updater.exe"), "OLD");
        File.WriteAllText(Path.Combine(_root, "vrcresolver.Updater.next.exe"), "NEW");
        File.WriteAllText(Path.Combine(_root, "WKVRCProxy.Updater.exe"), "OLD-REAL-UPDATER");
        File.WriteAllText(Path.Combine(_root, "WKVRCProxy.Updater.next.exe"), "LAUNCHER");

        Assert.True(UpdaterRepair.ApplyIfPresent(_root));
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(_root, "vrcresolver.Updater.exe")));
        Assert.Equal("LAUNCHER", File.ReadAllText(Path.Combine(_root, "WKVRCProxy.Updater.exe")));
    }

    [Fact]
    public void ApplyIfPresent_noops_when_no_staged_copy_exists()
    {
        File.WriteAllText(Path.Combine(_root, "vrcresolver.Updater.exe"), "OLD");

        bool applied = UpdaterRepair.ApplyIfPresent(_root);

        Assert.False(applied);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(_root, "vrcresolver.Updater.exe")));
    }
}
