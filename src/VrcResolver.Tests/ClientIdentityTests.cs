using Xunit;

namespace VrcResolver.Tests;

// Per-install client identity is the server-side analytics anchor for
// playback_feedback + v3 client_hello. A regression that re-generates
// the GUID on every launch silently breaks "is this the same watchdog
// returning?" — server-side reports would see one short-lived client
// per launch instead of one long-lived client.
//
// All operations on ClientIdentity.LoadOrCreate are best-effort —
// permissions / corruption / missing-dir paths must never throw.
public class ClientIdentityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public ClientIdentityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vrcresolver-tests-clientid-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, ClientIdentity.FileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void LoadOrCreate_FirstCall_GeneratesAndPersists()
    {
        Assert.False(File.Exists(_path));
        string id = ClientIdentity.LoadOrCreate(_path);
        // Generated GUID is 32 hex digits (N format).
        Assert.Matches("^[0-9a-fA-F]{32}$", id);
        Assert.True(File.Exists(_path));
        Assert.Equal(id, File.ReadAllText(_path).Trim());
    }

    [Fact]
    public void LoadOrCreate_SubsequentCalls_ReturnSameIdentity()
    {
        // The whole point of the persistence: a returning watchdog
        // presents the same client_id across launches.
        string first = ClientIdentity.LoadOrCreate(_path);
        string second = ClientIdentity.LoadOrCreate(_path);
        string third = ClientIdentity.LoadOrCreate(_path);
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void LoadOrCreate_CorruptFile_RegeneratesAndOverwrites()
    {
        // File present but contents not a parseable GUID — regenerate
        // rather than failing. Returns a fresh GUID and overwrites the
        // bad file so subsequent launches get the new identity.
        File.WriteAllText(_path, "not-a-guid");
        string id = ClientIdentity.LoadOrCreate(_path);
        Assert.Matches("^[0-9a-fA-F]{32}$", id);
        Assert.Equal(id, File.ReadAllText(_path).Trim());

        // Subsequent call returns the regenerated id, not another fresh one.
        string id2 = ClientIdentity.LoadOrCreate(_path);
        Assert.Equal(id, id2);
    }

    [Fact]
    public void LoadOrCreate_FileWithLeadingTrailingWhitespace_AcceptedAfterTrim()
    {
        // Hand-edited or copy-pasted GUIDs may carry stray whitespace.
        // Trim before parsing — don't make a user re-init their identity
        // because Notepad added a trailing newline.
        var g = Guid.NewGuid();
        File.WriteAllText(_path, "  " + g.ToString("N") + "\r\n");
        string id = ClientIdentity.LoadOrCreate(_path);
        Assert.Equal(g.ToString("N"), id);
    }

    [Fact]
    public void LoadOrCreate_GuidWithDashesFormat_RejectedAsCorrupt()
    {
        // Spec: "N" format (32 hex, no dashes). Server-side analytics may
        // string-match expecting that exact shape. A dashed GUID
        // (D format, 36 chars) is treated as corrupt and regenerated to
        // the canonical N form.
        var g = Guid.NewGuid();
        File.WriteAllText(_path, g.ToString("D"));
        string id = ClientIdentity.LoadOrCreate(_path);
        Assert.NotEqual(g.ToString("N"), id);
        // New id, in N form.
        Assert.Matches("^[0-9a-fA-F]{32}$", id);
    }

    [Fact]
    public void LoadOrCreate_AtomicWrite_LeavesNoTmpResidue()
    {
        // Write-via-tmp should clean up: no .new sidecar after Store.
        ClientIdentity.LoadOrCreate(_path);
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".new"));
    }

    [Fact]
    public void LoadOrCreate_MissingParentDir_StillReturnsValidGuid()
    {
        // Parent dir is created on demand. If create-dir itself fails
        // (locked-down filesystem), generation degrades to in-memory
        // only and STILL returns a valid GUID rather than throwing.
        string deepPath = Path.Combine(_tempDir, "subdir", "nested", ClientIdentity.FileName);
        Assert.False(Directory.Exists(Path.GetDirectoryName(deepPath)!));
        string id = ClientIdentity.LoadOrCreate(deepPath);
        Assert.Matches("^[0-9a-fA-F]{32}$", id);
        // On a normal filesystem this also persisted.
        Assert.True(File.Exists(deepPath));
    }

    [Fact]
    public void LoadOrCreate_OversizeFile_RegeneratesFresh()
    {
        // Hardening: a multi-GB file at the client_id path must NOT be
        // pumped through File.ReadAllText (would alloc full file before
        // catch fires). Cap is FileInfo.Length pre-check; oversize
        // fall-through regenerates a fresh GUID and overwrites in place.
        long cap = ClientIdentity.MaxClientIdFileBytes;
        File.WriteAllBytes(_path, new byte[cap + 1]);

        string id = ClientIdentity.LoadOrCreate(_path);

        // Returned GUID is fresh + valid.
        Assert.Matches("^[0-9a-fA-F]{32}$", id);
        // File on disk is now the fresh GUID, not the oversize blob.
        Assert.Equal(id, File.ReadAllText(_path).Trim());
        Assert.Equal(32, new FileInfo(_path).Length);
    }

    [Fact]
    public void LoadOrCreate_SaveFailure_CleansTmpResidue()
    {
        // Simulate a save failure by holding the destination open with
        // FileShare.None so File.Move can't overwrite it. The catch
        // block must delete the .new tmp it just created.
        // First seed a real client_id so File.Move's destination exists.
        string seeded = ClientIdentity.LoadOrCreate(_path);
        Assert.True(File.Exists(_path));

        using (var lockFs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Plant something invalid at the path's content shape so the
            // load path goes to the regenerate branch — but we can't
            // overwrite the locked file from the test, so instead force
            // the regenerate by deleting client_id.txt would also lock.
            // Simpler: corrupt the LOADED content via a sibling path.
            // We can't easily exercise the real path here; assert the
            // baseline: after a clean LoadOrCreate the tmp doesn't
            // linger.
        }

        Assert.False(File.Exists(_path + ".new"),
            "After successful LoadOrCreate, no tmp residue should remain");

        // Re-run LoadOrCreate while holding the destination locked. The
        // load path falls through to regenerate; the write path's
        // File.Move(tmp, _path, overwrite:true) will fail because the
        // path is locked. Outer catch must delete the .new tmp.
        using (var lockFs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Force the regen branch: provide a corrupted file mid-call.
            // Since we hold the lock, ReadAllText will throw and fall to
            // regenerate. The write will fail on File.Move. Catch block
            // must clean up.
            string id = ClientIdentity.LoadOrCreate(_path);
            // Returned GUID is fresh (in-memory only).
            Assert.Matches("^[0-9a-fA-F]{32}$", id);
        }

        Assert.False(File.Exists(_path + ".new"),
            "LoadOrCreate catch should have deleted the .new tmp residue");
    }
}
