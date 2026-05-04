using WKVRCProxy.Shared;
using Xunit;

namespace WKVRCProxy.Tests;

// ToolsDirSweeper.Sweep matches strictly the watchdog's own sidecar
// patterns (.new-<short>, .stale-<utc>) and leaves everything else
// alone. A regex regression here would either accumulate sidecars
// or accidentally delete unrelated files VRChat dropped in Tools.
public class ToolsDirSweeperTests : IDisposable
{
    private readonly string _tempDir;

    public ToolsDirSweeperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wkvrcproxy-tests-sweeper-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string Touch(string name)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Sweep_deletes_known_sidecars_and_leaves_everything_else()
    {
        // Sidecars our code creates.
        Touch("yt-dlp.exe.new-ab12cd34");
        Touch("yt-dlp.exe.stale-20260503120000123");
        Touch("yt-dlp-og.exe.new-ef56gh78");
        Touch("yt-dlp-og.exe.stale-20260503120000456");

        // Files that look similar but should NOT be swept.
        Touch("yt-dlp.exe");
        Touch("yt-dlp-og.exe");
        Touch("yt-dlp.exe.bak");
        Touch("yt-dlp.exe.config");
        Touch("yt-dlp.exe.log");
        Touch("yt-dlp-patched.exe.new-xyz123ab"); // not our naming
        Touch("ytdlp.exe.new-ab12cd34");          // missing dash
        Touch("README.txt");
        Touch("VRChat.log");

        ToolsDirSweeper.Sweep(_tempDir);

        var survivors = Directory.GetFiles(_tempDir).Select(Path.GetFileName).Order().ToArray();
        var expected = new[]
        {
            "README.txt",
            "VRChat.log",
            "yt-dlp-og.exe",
            "yt-dlp-patched.exe.new-xyz123ab",
            "yt-dlp.exe",
            "yt-dlp.exe.bak",
            "yt-dlp.exe.config",
            "yt-dlp.exe.log",
            "ytdlp.exe.new-ab12cd34",
        };
        Assert.Equal(expected, survivors);
    }

    [Fact]
    public void Sweep_handles_missing_directory_silently()
    {
        ToolsDirSweeper.Sweep(Path.Combine(_tempDir, "does-not-exist")); // should not throw
        ToolsDirSweeper.Sweep(null); // null path
        ToolsDirSweeper.Sweep("");   // empty path
    }

    [Fact]
    public void Sweep_is_case_insensitive()
    {
        // NTFS is case-insensitive but case-preserving. Verify the regex
        // doesn't fail on uppercase variants that VRChat or another tool
        // might produce.
        Touch("YT-DLP.EXE.NEW-ABCD1234");
        Touch("yt-dlp.EXE.STALE-20260503");

        ToolsDirSweeper.Sweep(_tempDir);

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void Sweep_deletes_legacy_yt_dlp_wrapper_log()
    {
        // Earlier wrapper builds wrote yt-dlp-wrapper.log into VRChat's
        // Tools dir. Current code writes it under %LOCALAPPDATA%, but the
        // sweeper must still scrub the literal residue for users updating
        // from those builds.
        Touch("yt-dlp-wrapper.log");
        Touch("YT-DLP-WRAPPER.LOG");
        Touch("yt-dlp-wrapper.log.bak"); // similar but not the literal — keep
        Touch("yt-dlp.exe");

        ToolsDirSweeper.Sweep(_tempDir);

        var survivors = Directory.GetFiles(_tempDir).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "yt-dlp-wrapper.log.bak", "yt-dlp.exe" }, survivors);
    }
}
