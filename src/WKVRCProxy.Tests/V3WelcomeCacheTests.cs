using WKVRCProxy.Shared;
using Xunit;

namespace WKVRCProxy.Tests;

// Per-node welcome-cache persistence. The cache file is the only state
// the v3 handshake holds across launches; a corrupt or missing file
// MUST gracefully degrade to "send null hash, take the full welcome on
// the wire" rather than throwing — the v3 wire path assumes cache is
// optional.
public class V3WelcomeCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public V3WelcomeCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wkvrcproxy-tests-v3cache-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "v3_welcome_cache.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Get_on_missing_file_returns_null()
    {
        var cache = new WelcomeCache(_path);
        Assert.Null(cache.Get("node1.whyknot.dev"));
    }

    [Fact]
    public void Store_then_Get_round_trips_per_node()
    {
        var cache = new WelcomeCache(_path);
        var welcome1 = new WelcomeFrame
        {
            ProtocolVersion = 3,
            Node = "node1",
            Engines = new[] { "yt-dlp" },
            Features = new[] { WireConstants.FeatureV3Compression },
            WarpActive = true,
            ServerVersion = "v1",
            YtDlpVersion = "yd1",
        };
        var welcome2 = new WelcomeFrame
        {
            ProtocolVersion = 3,
            Node = "node2",
            Engines = new[] { "yt-dlp", "ffmpeg" },
            Features = new[] { WireConstants.FeatureV3Compression, WireConstants.FeatureWelcomeHashAck },
            WarpActive = false,
            ServerVersion = "v2",
            YtDlpVersion = "yd2",
        };
        cache.Store("node1.whyknot.dev", welcome1, "hash1");
        cache.Store("node2.whyknot.dev", welcome2, "hash2");

        // Re-open the cache from disk to confirm the persistence layer
        // (not just an in-memory dict).
        var reopened = new WelcomeCache(_path);
        var got1 = reopened.Get("node1.whyknot.dev");
        var got2 = reopened.Get("node2.whyknot.dev");
        Assert.NotNull(got1);
        Assert.NotNull(got2);
        Assert.Equal("hash1", got1!.WelcomeHash);
        Assert.Equal("node1", got1.Node);
        Assert.Equal(true, got1.WarpActive);
        Assert.Equal("hash2", got2!.WelcomeHash);
        Assert.Equal(2, got2.Engines!.Length);
        Assert.Equal(false, got2.WarpActive);
    }

    [Fact]
    public void Store_for_one_node_does_not_evict_another()
    {
        // Per-node keying: writing node2's welcome must not blow away
        // node1's. Single-slot bug regression test.
        var cache = new WelcomeCache(_path);
        cache.Store("node1.whyknot.dev", new WelcomeFrame { ProtocolVersion = 3, Node = "node1" }, "h1");
        cache.Store("node2.whyknot.dev", new WelcomeFrame { ProtocolVersion = 3, Node = "node2" }, "h2");
        Assert.Equal("h1", cache.Get("node1.whyknot.dev")?.WelcomeHash);
        Assert.Equal("h2", cache.Get("node2.whyknot.dev")?.WelcomeHash);
    }

    [Fact]
    public void Invalidate_removes_only_the_named_node()
    {
        var cache = new WelcomeCache(_path);
        cache.Store("node1.whyknot.dev", new WelcomeFrame { ProtocolVersion = 3 }, "h1");
        cache.Store("node2.whyknot.dev", new WelcomeFrame { ProtocolVersion = 3 }, "h2");
        cache.Invalidate("node1.whyknot.dev");
        Assert.Null(cache.Get("node1.whyknot.dev"));
        Assert.NotNull(cache.Get("node2.whyknot.dev"));
    }

    [Fact]
    public void Get_on_corrupt_file_returns_null()
    {
        // Corrupt JSON should NOT throw — the v3 handshake treats it as
        // cache miss and continues.
        File.WriteAllText(_path, "{ this is not valid json");
        var cache = new WelcomeCache(_path);
        Assert.Null(cache.Get("node1.whyknot.dev"));
    }

    [Fact]
    public void Store_writes_atomically_via_tmp_then_rename()
    {
        // After Store, no .new sidecar should remain. Earlier non-atomic
        // implementations could leave the .new file alongside on a crash;
        // confirm File.Move(overwrite:true) replaces the destination
        // cleanly.
        var cache = new WelcomeCache(_path);
        cache.Store("node1", new WelcomeFrame { ProtocolVersion = 3 }, "h");
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".new"));
    }

    [Fact]
    public void Get_on_empty_or_null_node_returns_null()
    {
        var cache = new WelcomeCache(_path);
        Assert.Null(cache.Get(""));
        Assert.Null(cache.Get(null!));
    }

    [Fact]
    public void Store_on_empty_hash_is_noop()
    {
        // Defensive: never persist an entry without a hash — a hashless
        // entry can't satisfy the next handshake's lookup anyway.
        var cache = new WelcomeCache(_path);
        cache.Store("node1", new WelcomeFrame { ProtocolVersion = 3 }, "");
        Assert.Null(cache.Get("node1"));
        Assert.False(File.Exists(_path));
    }
}
