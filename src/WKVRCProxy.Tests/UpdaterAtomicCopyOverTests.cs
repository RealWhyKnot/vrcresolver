using Xunit;
using UpdaterProgram = WKVRCProxy.Updater.Program;

namespace WKVRCProxy.Tests;

// AtomicCopyOver's two-pass stage + rename + rollback. Critical: when
// File.Move(tmp, dst) throws midway through the rename pass, the
// previously-renamed pairs must be restored from .old-<short> sidecars.
// Pre-fix the rollback list was populated AFTER the move that could
// throw, leaving an orphan backup if the move failed.
public class UpdaterAtomicCopyOverTests : IDisposable
{
    private readonly string _from;
    private readonly string _to;
    private readonly string _root;

    public UpdaterAtomicCopyOverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wkvrcproxy-tests-cpyover-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        _from = Path.Combine(_root, "from");
        _to = Path.Combine(_root, "to");
        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_to);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Happy_path_overwrites_targets_and_drops_backups()
    {
        File.WriteAllText(Path.Combine(_to, "WKVRCProxy.exe"), "OLD");
        Directory.CreateDirectory(Path.Combine(_to, "tools"));
        File.WriteAllText(Path.Combine(_to, "tools/yt-dlp-og-fallback.exe"), "OLD-OG");

        File.WriteAllText(Path.Combine(_from, "WKVRCProxy.exe"), "NEW");
        Directory.CreateDirectory(Path.Combine(_from, "tools"));
        File.WriteAllText(Path.Combine(_from, "tools/yt-dlp-og-fallback.exe"), "NEW-OG");

        UpdaterProgram.AtomicCopyOver(_from, _to);

        Assert.Equal("NEW", File.ReadAllText(Path.Combine(_to, "WKVRCProxy.exe")));
        Assert.Equal("NEW-OG", File.ReadAllText(Path.Combine(_to, "tools/yt-dlp-og-fallback.exe")));

        // No .old-<short> or .new-<short> sidecars should remain in the install dir.
        var sidecars = Directory.GetFiles(_to, "*.old-*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(_to, "*.new-*", SearchOption.AllDirectories))
            .ToArray();
        Assert.Empty(sidecars);
    }

    [Fact]
    public void Skips_overwriting_running_updater()
    {
        File.WriteAllText(Path.Combine(_to, "WKVRCProxy.Updater.exe"), "OLD-UPDATER");
        File.WriteAllText(Path.Combine(_to, "WKVRCProxy.exe"), "OLD-WATCHDOG");

        File.WriteAllText(Path.Combine(_from, "WKVRCProxy.Updater.exe"), "NEW-UPDATER");
        File.WriteAllText(Path.Combine(_from, "WKVRCProxy.exe"), "NEW-WATCHDOG");

        UpdaterProgram.AtomicCopyOver(_from, _to);

        // Updater is preserved (we can't overwrite our own running exe).
        Assert.Equal("OLD-UPDATER", File.ReadAllText(Path.Combine(_to, "WKVRCProxy.Updater.exe")));
        // Watchdog gets the new version.
        Assert.Equal("NEW-WATCHDOG", File.ReadAllText(Path.Combine(_to, "WKVRCProxy.exe")));
    }

    [Fact]
    public void Rollback_restores_originals_on_rename_failure_mid_pass()
    {
        // Three files, the middle one will be locked so the rename pass
        // fails on it. The first one's rename should be rolled back.
        File.WriteAllText(Path.Combine(_to, "a.bin"), "A-OLD");
        File.WriteAllText(Path.Combine(_to, "b.bin"), "B-OLD");
        File.WriteAllText(Path.Combine(_to, "c.bin"), "C-OLD");

        File.WriteAllText(Path.Combine(_from, "a.bin"), "A-NEW");
        File.WriteAllText(Path.Combine(_from, "b.bin"), "B-NEW");
        File.WriteAllText(Path.Combine(_from, "c.bin"), "C-NEW");

        // Lock b.bin with FileShare.None so File.Move(b, b.old-XYZ)
        // throws — trips the rollback. Release the handle before reading
        // back any state so the assertions don't hit our own lock.
        var lockHandle = new FileStream(
            Path.Combine(_to, "b.bin"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        try
        {
            Assert.ThrowsAny<Exception>(() => UpdaterProgram.AtomicCopyOver(_from, _to));
        }
        finally
        {
            lockHandle.Dispose();
        }

        // The order of rename inside AtomicCopyOver follows
        // Directory.GetFiles enumeration, which on NTFS is ordinal name
        // order. So a.bin renames first; b.bin's move-aside fails;
        // rollback restores a.bin from a.bin.old-<short>.
        Assert.Equal("A-OLD", File.ReadAllText(Path.Combine(_to, "a.bin")));
        Assert.Equal("B-OLD", File.ReadAllText(Path.Combine(_to, "b.bin")));
        Assert.Equal("C-OLD", File.ReadAllText(Path.Combine(_to, "c.bin")));

        // No .old-<short> backups should remain after rollback.
        var oldSidecars = Directory.GetFiles(_to, "*.old-*").ToArray();
        Assert.Empty(oldSidecars);
    }

    [Fact]
    public void Rollback_cleans_up_staged_tmps_on_pre_rename_failure()
    {
        // If a tmp can't be created (e.g. dst dir doesn't exist on a
        // sub-path), the stage pass throws before the rename pass even
        // starts. Already-created tmps must be cleaned up.
        Directory.CreateDirectory(Path.Combine(_from, "subdir"));
        File.WriteAllText(Path.Combine(_from, "a.bin"), "A");
        File.WriteAllText(Path.Combine(_from, "subdir/b.bin"), "B");

        // _to exists, _to/subdir does not — AtomicCopyOver should
        // create it via Directory.CreateDirectory. So this test
        // actually exercises the happy path of subdirectory creation.
        UpdaterProgram.AtomicCopyOver(_from, _to);
        Assert.Equal("A", File.ReadAllText(Path.Combine(_to, "a.bin")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(_to, "subdir/b.bin")));
    }
}
