using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

public class UpdaterRepairTests : IDisposable
{
    private readonly string _root;

    public UpdaterRepairTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wkvrcproxy-tests-updater-repair-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ApplyIfPresent_replaces_stale_updater_with_staged_copy()
    {
        File.WriteAllText(Path.Combine(_root, "WKVRCProxy.Updater.exe"), "OLD");
        File.WriteAllText(Path.Combine(_root, "WKVRCProxy.Updater.next.exe"), "NEW");

        bool applied = UpdaterRepair.ApplyIfPresent(_root);

        Assert.True(applied);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(_root, "WKVRCProxy.Updater.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "WKVRCProxy.Updater.next.exe")));
        Assert.Empty(Directory.GetFiles(_root, "*.old-*"));
    }

    [Fact]
    public void ApplyIfPresent_noops_when_no_staged_copy_exists()
    {
        File.WriteAllText(Path.Combine(_root, "WKVRCProxy.Updater.exe"), "OLD");

        bool applied = UpdaterRepair.ApplyIfPresent(_root);

        Assert.False(applied);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(_root, "WKVRCProxy.Updater.exe")));
    }
}
