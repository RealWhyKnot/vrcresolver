using System.Runtime.Versioning;
using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

// PatchManager.RestoreYtDlpInTools needs to handle a target-locked
// scenario by moving the locked file aside (.stale-<utc>) and
// promoting the og backup. A regression here would leave VRChat with
// a still-patched yt-dlp.exe after watchdog shutdown — the user
// invariant violation called out in the cleanup-invariant audit.
[SupportedOSPlatform("windows")]
public class PatchManagerRestoreTests : IDisposable
{
    private readonly string _toolsDir;

    public PatchManagerRestoreTests()
    {
        _toolsDir = Path.Combine(Path.GetTempPath(), "wkvrcproxy-tests-restore-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_toolsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_toolsDir, recursive: true); } catch { /* best-effort */ }
    }

    private string TargetPath => Path.Combine(_toolsDir, "yt-dlp.exe");
    private string BackupPath => Path.Combine(_toolsDir, "yt-dlp-og.exe");

    [Fact]
    public void Restore_succeeds_via_atomic_move_when_target_unlocked()
    {
        File.WriteAllText(TargetPath, "PATCHED");
        File.WriteAllText(BackupPath, "VANILLA");

        Assert.True(PatchManager.RestoreYtDlpInTools(_toolsDir));
        Assert.True(File.Exists(TargetPath));
        Assert.False(File.Exists(BackupPath));
        Assert.Equal("VANILLA", File.ReadAllText(TargetPath));

        // No stale-aside should have been created on the happy path.
        var siblings = Directory.GetFiles(_toolsDir);
        Assert.Single(siblings);
    }

    [Fact]
    public void Restore_returns_false_when_target_is_fully_locked()
    {
        File.WriteAllText(TargetPath, "PATCHED");
        File.WriteAllText(BackupPath, "VANILLA");

        // Hold the target with FileShare.None — both the fast-path
        // overwrite-move and the stale-aside Move(target, stale) throw.
        // RestoreYtDlpInTools returns false; backup stays put for a
        // future retry. Release the handle before assertions so they
        // can read the backup file.
        var lockHandle = new FileStream(TargetPath, FileMode.Open, FileAccess.Read, FileShare.None);
        bool ok;
        try
        {
            ok = PatchManager.RestoreYtDlpInTools(_toolsDir);
        }
        finally
        {
            lockHandle.Dispose();
        }
        Assert.False(ok);
        // Backup should still be present so a future retry can succeed.
        Assert.True(File.Exists(BackupPath));
    }

    [Fact]
    public void Restore_returns_false_when_backup_missing()
    {
        File.WriteAllText(TargetPath, "PATCHED");
        // No backup written.

        bool ok = PatchManager.RestoreYtDlpInTools(_toolsDir);
        Assert.False(ok);
        // Target is left untouched — caller can't restore from nothing.
        Assert.True(File.Exists(TargetPath));
    }

    [Fact]
    public void Restore_handles_missing_directory()
    {
        bool ok = PatchManager.RestoreYtDlpInTools(Path.Combine(_toolsDir, "does-not-exist"));
        Assert.False(ok);
    }

    [Fact]
    public void AtomicCopy_replaces_existing_destination_atomically()
    {
        string src = Path.Combine(_toolsDir, "source.bin");
        string dst = Path.Combine(_toolsDir, "dest.bin");
        File.WriteAllText(src, "NEW");
        File.WriteAllText(dst, "OLD");

        PatchManager.AtomicCopy(src, dst);

        Assert.Equal("NEW", File.ReadAllText(dst));
        // No .new-<short> tmp left behind.
        var leftovers = Directory.GetFiles(_toolsDir, "dest.bin.new-*");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void AtomicCopy_cleans_up_tmp_on_failure()
    {
        string src = Path.Combine(_toolsDir, "missing.bin");
        string dst = Path.Combine(_toolsDir, "dest.bin");
        File.WriteAllText(dst, "OLD");

        // Source missing — File.Copy throws inside AtomicCopy; the catch
        // should delete the tmp before rethrowing.
        Assert.ThrowsAny<Exception>(() => PatchManager.AtomicCopy(src, dst));

        // Destination unchanged, no tmp leftovers.
        Assert.Equal("OLD", File.ReadAllText(dst));
        var leftovers = Directory.GetFiles(_toolsDir, "dest.bin.new-*");
        Assert.Empty(leftovers);
    }

    // The crash-prevention probe: VRChat mid-CreateProcess on yt-dlp.exe
    // takes a FileShare.Read handle (or similar), so our FileShare.None
    // probe must report "in use" and the tick must defer. Inversely, an
    // unheld file must report "not in use" so normal patch operations
    // proceed without spurious deferrals.
    [Fact]
    public void IsTargetInUse_returns_false_when_no_handle_held()
    {
        File.WriteAllText(TargetPath, "stub");
        Assert.False(PatchManager.IsTargetInUse(TargetPath));
    }

    [Fact]
    public void IsTargetInUse_returns_true_when_held_with_FileShare_None()
    {
        File.WriteAllText(TargetPath, "stub");
        using var holder = new FileStream(TargetPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(PatchManager.IsTargetInUse(TargetPath));
    }

    [Fact]
    public void IsTargetInUse_returns_true_when_held_with_FileShare_Read()
    {
        // Mirrors the VRChat CreateProcess scenario: VRChat opens yt-dlp
        // with FileShare.Read (the Windows loader's typical mode for an
        // executable being mapped). Our FileShare.None probe must fail
        // with sharing violation and report "in use".
        File.WriteAllText(TargetPath, "stub");
        using var holder = new FileStream(TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(PatchManager.IsTargetInUse(TargetPath));
    }

    [Fact]
    public void IsTargetInUse_returns_true_when_held_with_FileShare_ReadWrite()
    {
        // Auto-update / browser-download scenario: another tool is mid-write
        // on yt-dlp.exe. Probe must defer rather than racing the writer.
        File.WriteAllText(TargetPath, "stub");
        using var holder = new FileStream(TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.True(PatchManager.IsTargetInUse(TargetPath));
    }

    [Fact]
    public void IsTargetInUse_returns_false_when_path_missing()
    {
        // Defer-on-missing would block patch attempts forever for legitimately
        // absent paths (initial install, mid-rename window). Return false so
        // the caller's File.Exists / IOException branches handle it.
        Assert.False(PatchManager.IsTargetInUse(Path.Combine(_toolsDir, "does-not-exist.exe")));
    }

    [Fact]
    public void IsTargetInUse_returns_false_when_directory_missing()
    {
        Assert.False(PatchManager.IsTargetInUse(Path.Combine(_toolsDir, "no-such-dir", "yt-dlp.exe")));
    }
}
