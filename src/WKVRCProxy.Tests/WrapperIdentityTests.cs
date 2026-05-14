using System.Runtime.Versioning;
using System.Text;
using WKVRCProxy.Shared;
using Xunit;

namespace WKVRCProxy.Tests;

// The four signals are tested independently: marker scan, PE FileVersionInfo
// (covered live by integration test against the built wrapper -- can't be
// faked from a test fixture), hash list, and size band. The recursive-exec
// guard in the wrapper depends on this classifier returning `Ours` reliably
// when handed our own binary path, which is the failure mode each marker /
// hash / size test guards against.
[SupportedOSPlatform("windows")]
public class WrapperIdentityTests : IDisposable
{
    private readonly string _scratchDir;

    public WrapperIdentityTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "wkvrcproxy-tests-identity-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchDir, recursive: true); } catch { /* best-effort */ }
    }

    private string MakeFile(string name, byte[] content)
    {
        string path = Path.Combine(_scratchDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] WithMarkerAt(int totalSize, int markerOffset)
    {
        byte[] marker = Encoding.UTF8.GetBytes(WrapperIdentity.Marker);
        if (markerOffset + marker.Length > totalSize)
            throw new ArgumentException("marker would not fit");
        byte[] content = new byte[totalSize];
        // Fill with non-zero pattern so a zeroed scan buffer can't false-positive.
        for (int i = 0; i < totalSize; i++) content[i] = (byte)((i * 31) & 0xFF);
        marker.CopyTo(content, markerOffset);
        return content;
    }

    [Fact]
    public void ContainsMarker_true_when_marker_embedded()
    {
        string path = MakeFile("with-marker.bin", WithMarkerAt(4096, 1234));
        Assert.True(WrapperIdentity.ContainsMarker(path));
    }

    [Fact]
    public void ContainsMarker_false_for_random_bytes()
    {
        byte[] content = new byte[4096];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)((i * 17) & 0xFF);
        string path = MakeFile("no-marker.bin", content);
        Assert.False(WrapperIdentity.ContainsMarker(path));
    }

    [Fact]
    public void ContainsMarker_false_for_missing_file()
    {
        Assert.False(WrapperIdentity.ContainsMarker(Path.Combine(_scratchDir, "does-not-exist.bin")));
    }

    [Fact]
    public void ContainsMarker_false_for_empty_file()
    {
        string path = MakeFile("empty.bin", Array.Empty<byte>());
        Assert.False(WrapperIdentity.ContainsMarker(path));
    }

    [Fact]
    public void ContainsMarker_false_for_file_over_scan_cap()
    {
        // 17 MiB > MaxScanBytes (16 MiB). Marker physically present near the
        // start, but the helper bails on the size check without reading.
        // Anything that large can't be ours anyway -- the AOT wrapper is < 10 MiB.
        const int bigSize = WrapperIdentity.MaxScanBytes + 1024 * 1024;
        byte[] content = WithMarkerAt(bigSize, 256);
        string path = MakeFile("too-large.bin", content);
        Assert.False(WrapperIdentity.ContainsMarker(path));
    }

    [Fact]
    public void Classify_Ours_when_marker_present_in_small_file()
    {
        string path = MakeFile("small-ours.bin", WithMarkerAt(8192, 256));
        Assert.Equal(WrapperKind.Ours, WrapperIdentity.Classify(path));
    }

    [Fact]
    public void Classify_VrcBundledYtDlp_when_large_unknown_binary()
    {
        // Above OursSizeCeiling (10 MiB) with no marker, no PE metadata, no
        // hash match -> classified as VRChat-bundled yt-dlp by elimination.
        byte[] content = new byte[(int)WrapperIdentity.OursSizeCeiling + 4096];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)((i * 7) & 0xFF);
        string path = MakeFile("large-unknown.bin", content);
        Assert.Equal(WrapperKind.VrcBundledYtDlp, WrapperIdentity.Classify(path));
    }

    [Fact]
    public void Classify_Unknown_when_small_unknown_binary()
    {
        // Below the size ceiling and missing every positive signal: refuse
        // to act. PatchManager treats Unknown as "wait, don't mutate" --
        // a misclassification as VrcBundledYtDlp here would risk exec'ing
        // an arbitrary small file in the fallback path.
        byte[] content = new byte[3 * 1024 * 1024];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)((i * 13) & 0xFF);
        string path = MakeFile("small-unknown.bin", content);
        Assert.Equal(WrapperKind.Unknown, WrapperIdentity.Classify(path));
    }

    [Fact]
    public void Classify_Unknown_when_file_missing()
    {
        Assert.Equal(WrapperKind.Unknown, WrapperIdentity.Classify(Path.Combine(_scratchDir, "ghost.bin")));
    }

    [Fact]
    public void Classify_Ours_when_hash_in_known_list()
    {
        // Build a known-release hash file that includes the candidate's SHA.
        // No marker, no PE -- pure hash-list hit.
        byte[] content = new byte[2 * 1024 * 1024];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)((i * 11) & 0xFF);
        string candidatePath = MakeFile("released-prior-wrapper.bin", content);
        string? sha = WrapperIdentity.ComputeSha256(candidatePath);
        Assert.NotNull(sha);

        string hashListPath = Path.Combine(_scratchDir, "known_wrapper_hashes.txt");
        File.WriteAllText(hashListPath, $"# old prior-release wrappers\n{sha}  2026.5.10.4  2026-05-10T00:00:00Z\n");

        Assert.Equal(WrapperKind.Ours, WrapperIdentity.Classify(candidatePath, hashListPath));
    }

    [Fact]
    public void Classify_does_not_consult_hash_list_when_missing()
    {
        byte[] content = new byte[3 * 1024 * 1024];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)i;
        string path = MakeFile("nohashlist.bin", content);
        // Passing a path to a missing file should not throw and should not
        // suddenly classify the candidate as Ours.
        Assert.Equal(WrapperKind.Unknown, WrapperIdentity.Classify(path, Path.Combine(_scratchDir, "no-such-list.txt")));
    }

    [Fact]
    public void KnownHashListContains_ignores_comments_and_blanks()
    {
        string listPath = Path.Combine(_scratchDir, "list.txt");
        File.WriteAllText(listPath, "# header comment\n\n  \nabc123  v1  2026-01-01\n# trailing\n");
        Assert.True(WrapperIdentity.KnownHashListContains(listPath, "abc123"));
        Assert.False(WrapperIdentity.KnownHashListContains(listPath, "deadbeef"));
    }

    [Fact]
    public void KnownHashListContains_case_insensitive_on_sha()
    {
        string listPath = Path.Combine(_scratchDir, "list.txt");
        File.WriteAllText(listPath, "AbC123  v1  2026-01-01\n");
        Assert.True(WrapperIdentity.KnownHashListContains(listPath, "abc123"));
        Assert.True(WrapperIdentity.KnownHashListContains(listPath, "ABC123"));
    }

    [Fact]
    public void ComputeSha256_matches_known_value()
    {
        // SHA-256("") = e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        string path = MakeFile("empty.bin", Array.Empty<byte>());
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", WrapperIdentity.ComputeSha256(path));
    }
}
