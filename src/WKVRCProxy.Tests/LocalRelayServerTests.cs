using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

[SupportedOSPlatform("windows")]
public class LocalRelayServerTests
{
    [Fact]
    public void EncodeTargetParam_RoundTrips()
    {
        // Round-trip: encode + decode (via the server's private path
        // -- exercised indirectly by feeding encoded input back through
        // a constructed URL the rewriter would emit).
        string url = "https://node1.whyknot.dev/api/proxy?q=AbC-_dEf=&extra=hi%20there";
        string encoded = LocalRelayServer.EncodeTargetParam(url);

        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded);
        Assert.DoesNotContain("=", encoded);
        Assert.DoesNotContain(" ", encoded);

        // Manual decode mirroring the listener's DecodeTargetParam logic.
        string b64 = encoded.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        string decoded = Encoding.UTF8.GetString(System.Convert.FromBase64String(b64));
        Assert.Equal(url, decoded);
    }

    // ---- HlsManifestRewriter -------------------------------------------------

    [Fact]
    public void HlsRewrite_AllSegments_RoutedThroughListener()
    {
        // Every segment URL gets a /play?id=<12hex> wrap. NO bypass for
        // trust-listed hosts -- the architectural intent is that all bytes
        // route through whyknot.dev so WARP egress + central control are
        // preserved. The listener's id-registry compresses the wire encoding
        // to ~50 chars per segment (vs ~3000 for base64-target) so the
        // manifest stays small enough for AVPro to parse.
        var registry = new SegmentIdRegistry();
        string manifest =
            "#EXTM3U\n" +
            "#EXT-X-VERSION:3\n" +
            "#EXT-X-TARGETDURATION:6\n" +
            "#EXTINF:5.005,\n" +
            "https://node1.whyknot.dev/api/proxy?url=AAA\n" +
            "#EXTINF:5.005,\n" +
            "https://node1.whyknot.dev/api/proxy?url=BBB\n" +
            "#EXT-X-ENDLIST\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://node1.whyknot.dev/api/proxy?q=...", 51234, registry);

        // Segments rewrite to id-form. Original server-proxy URLs are gone
        // from the manifest body (they live in the registry instead).
        var idLines = rewritten.Split('\n')
            .Where(l => l.StartsWith("http://localhost.youtube.com:51234/play?id="))
            .ToList();
        Assert.Equal(2, idLines.Count);
        Assert.DoesNotContain("https://node1.whyknot.dev/api/proxy?url=AAA", rewritten);
        Assert.DoesNotContain("https://node1.whyknot.dev/api/proxy?url=BBB", rewritten);

        // Each ID is exactly 12 hex characters.
        foreach (var line in idLines)
        {
            string id = line.Substring("http://localhost.youtube.com:51234/play?id=".Length);
            Assert.Equal(12, id.Length);
            Assert.Matches("^[0-9a-f]{12}$", id);
        }

        // Both IDs registered + each maps back to the original URL.
        string id1 = idLines[0].Substring("http://localhost.youtube.com:51234/play?id=".Length);
        string id2 = idLines[1].Substring("http://localhost.youtube.com:51234/play?id=".Length);
        Assert.Equal("https://node1.whyknot.dev/api/proxy?url=AAA", registry.TryGetUrl(id1));
        Assert.Equal("https://node1.whyknot.dev/api/proxy?url=BBB", registry.TryGetUrl(id2));

        // Non-segment lines preserved.
        Assert.Contains("#EXTM3U", rewritten);
        Assert.Contains("#EXT-X-VERSION:3", rewritten);
        Assert.Contains("#EXT-X-TARGETDURATION:6", rewritten);
        Assert.Contains("#EXTINF:5.005", rewritten);
        Assert.Contains("#EXT-X-ENDLIST", rewritten);
    }

    [Fact]
    public void HlsRewrite_GooglevideoDirectSegments_AlsoRoutedThroughListener()
    {
        // Even when segments arrive on a host AVPro would accept directly
        // (googlevideo, etc.), the rewriter still routes them through the
        // listener. NO bypass. This is the architectural commitment: every
        // byte goes through whyknot.dev, no exceptions.
        var registry = new SegmentIdRegistry();
        string manifest =
            "#EXTM3U\n" +
            "#EXTINF:5.0,\n" +
            "https://r1.googlevideo.com/seg-1.ts\n" +
            "#EXTINF:5.0,\n" +
            "https://r1.googlevideo.com/seg-2.ts\n" +
            "#EXT-X-ENDLIST\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://r1.googlevideo.com/manifest.m3u8", 51234, registry);

        // No googlevideo URLs in the manifest body -- they're in the registry.
        Assert.DoesNotContain("https://r1.googlevideo.com/seg-1.ts", rewritten);
        Assert.DoesNotContain("https://r1.googlevideo.com/seg-2.ts", rewritten);

        // Both segments wrapped through localhost.
        Assert.Equal(2, rewritten.Split('\n')
            .Count(l => l.StartsWith("http://localhost.youtube.com:51234/play?id=")));

        // Registry has both URLs.
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void HlsRewrite_DuplicateSegmentUrl_GetsSameId()
    {
        // If a manifest references the same segment URL twice (e.g. byte-
        // range variants point at the same underlying file), the registry
        // returns the same id for both. Dedup is a fast-path optimization
        // for repeated lookups, not a correctness requirement.
        var registry = new SegmentIdRegistry();
        string manifest =
            "#EXTM3U\n" +
            "#EXTINF:5.0,\n" +
            "https://node1.whyknot.dev/api/proxy?url=ABCD\n" +
            "#EXTINF:5.0,\n" +
            "https://node1.whyknot.dev/api/proxy?url=ABCD\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://node1.whyknot.dev/api/proxy?q=...", 51234, registry);

        var idLines = rewritten.Split('\n')
            .Where(l => l.StartsWith("http://localhost.youtube.com:51234/play?id="))
            .ToList();
        Assert.Equal(2, idLines.Count);
        Assert.Equal(idLines[0], idLines[1]); // same URL -> same id.
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void HlsRewrite_RelativeSegmentUrl_ResolvesAgainstBase()
    {
        // Relative segment URLs resolve against the manifest's base URL,
        // then go through the listener wrap.
        var registry = new SegmentIdRegistry();
        string manifest =
            "#EXTM3U\n" +
            "#EXTINF:5.0,\n" +
            "seg-1.ts\n" +
            "#EXTINF:5.0,\n" +
            "subdir/seg-2.ts\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://node1.whyknot.dev/api/proxy?q=...", 51234, registry);

        // Both segments wrapped through localhost.
        Assert.Equal(2, rewritten.Split('\n')
            .Count(l => l.StartsWith("http://localhost.youtube.com:51234/play?id=")));
        Assert.Equal(2, registry.Count);

        // Registered URLs are absolute (resolved against the base).
        var registered = new HashSet<string>();
        foreach (var line in rewritten.Split('\n'))
        {
            if (!line.StartsWith("http://localhost.youtube.com:51234/play?id=")) continue;
            string id = line.Substring("http://localhost.youtube.com:51234/play?id=".Length);
            string? url = registry.TryGetUrl(id);
            Assert.NotNull(url);
            registered.Add(url!);
        }
        // Base URL path is `/api/proxy` so relative URLs resolve against `/api/`.
        Assert.Contains("https://node1.whyknot.dev/api/seg-1.ts", registered);
        Assert.Contains("https://node1.whyknot.dev/api/subdir/seg-2.ts", registered);
    }

    [Fact]
    public void HlsRewrite_UriAttribute_RoutedThroughListener()
    {
        // EXT-X-MAP, EXT-X-MEDIA URI=... attribute lines get the same wrap
        // as bare segment lines. Surrounding attributes (BYTERANGE, GROUP-ID,
        // NAME, etc.) preserved.
        var registry = new SegmentIdRegistry();
        string manifest =
            "#EXTM3U\n" +
            "#EXT-X-MAP:URI=\"https://node1.whyknot.dev/api/proxy?url=INIT\",BYTERANGE=\"800@0\"\n" +
            "#EXT-X-MEDIA:TYPE=AUDIO,URI=\"audio-eng.m3u8\",GROUP-ID=\"audio\",NAME=\"English\"\n" +
            "#EXTINF:5.0,\n" +
            "seg.ts\n";

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://node1.whyknot.dev/api/proxy?q=...", 51234, registry);

        Assert.Contains("URI=\"http://localhost.youtube.com:51234/play?id=", rewritten);
        Assert.DoesNotContain("URI=\"https://node1.whyknot.dev/api/proxy?url=INIT\"", rewritten);
        Assert.Contains("BYTERANGE=\"800@0\"", rewritten);
        Assert.Contains("GROUP-ID=\"audio\"", rewritten);
        Assert.Contains("NAME=\"English\"", rewritten);

        // Three URIs registered: the EXT-X-MAP init, the relative audio
        // playlist, and the bare seg.ts (all distinct).
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public void HlsRewrite_EmptyOrCommentLines_PassThrough()
    {
        var registry = new SegmentIdRegistry();
        string manifest = "#EXTM3U\n\n#EXT-X-VERSION:3\n";
        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://example.com/m.m3u8", 51234, registry);
        Assert.Equal(manifest, rewritten);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void HlsRewrite_NonUrlBareLine_StillRegisters()
    {
        // Defensive: a bare line that resolves against the manifest base
        // produces an absolute URL which the rewriter routes through the
        // listener. The cost of false-positive wrap is one 404 fetch when
        // AVPro tries to play it, not a parse failure.
        var registry = new SegmentIdRegistry();
        string manifest = "#EXTM3U\nrandom-non-url-text-that-isnt-a-segment\n";
        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://example.com/m.m3u8", 51234, registry);
        Assert.Contains("http://localhost.youtube.com:51234/play?id=", rewritten);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void HlsRewrite_LongFormManifest_StaysSmall()
    {
        // The motivating regression: a 1700-segment manifest. Each segment
        // URL is the server's `node1.whyknot.dev/api/proxy?url=<long-base64>`
        // shape. With the old rewriter this produced a 4.9 MB manifest that
        // broke AVPro. New rewriter compresses to ~50 chars per segment.
        var registry = new SegmentIdRegistry();
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n");
        const int segmentCount = 1700;
        // Simulate a realistically-large server-proxy-wrapped segment URL.
        // The actual URL the server emits is ~2050 chars for googlevideo.
        string longBase = "https://node1.whyknot.dev/api/proxy?url="
            + new string('A', 2000); // realistic server-emitted URL length
        for (int i = 0; i < segmentCount; i++)
        {
            sb.Append("#EXTINF:5.5,\n");
            sb.Append(longBase).Append(i).Append("\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        string manifest = sb.ToString();

        string rewritten = HlsManifestRewriter.Rewrite(
            manifest, "https://node1.whyknot.dev/api/proxy?q=...", 51234, registry);

        // Original manifest was ~3.5 MB; rewritten manifest should be
        // dramatically smaller because each segment URL becomes ~52 chars
        // (`http://localhost.youtube.com:51234/play?id=` + 12 hex).
        Assert.True(rewritten.Length < manifest.Length / 10,
            $"Rewritten manifest expected < manifest/10 = {manifest.Length / 10}, got {rewritten.Length}.");
        Assert.Equal(segmentCount, registry.Count);

        // Sanity: every original segment URL was registered.
        Assert.Equal(segmentCount, rewritten.Split('\n')
            .Count(l => l.StartsWith("http://localhost.youtube.com:51234/play?id=")));
    }

    // ---- SegmentIdRegistry ---------------------------------------------------

    [Fact]
    public void Registry_GetOrAddId_DedupesByUrl()
    {
        var r = new SegmentIdRegistry();
        string url = "https://node1.whyknot.dev/api/proxy?url=ABCD";
        string id1 = r.GetOrAddId(url);
        string id2 = r.GetOrAddId(url);
        Assert.Equal(id1, id2);
        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void Registry_GetOrAddId_DistinctUrlsGetDistinctIds()
    {
        var r = new SegmentIdRegistry();
        string id1 = r.GetOrAddId("https://example.com/a");
        string id2 = r.GetOrAddId("https://example.com/b");
        Assert.NotEqual(id1, id2);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void Registry_GetOrAddId_EmptyOrNullUrl_Throws()
    {
        var r = new SegmentIdRegistry();
        Assert.Throws<System.ArgumentException>(() => r.GetOrAddId(""));
        Assert.Throws<System.ArgumentException>(() => r.GetOrAddId(null!));
    }

    [Fact]
    public void Registry_GetOrAddId_IdShape_Is12HexChars()
    {
        var r = new SegmentIdRegistry();
        string id = r.GetOrAddId("https://example.com/a");
        Assert.Equal(12, id.Length);
        Assert.Matches("^[0-9a-f]{12}$", id);
    }

    [Fact]
    public void Registry_TryGetUrl_RoundTrips()
    {
        var r = new SegmentIdRegistry();
        string url = "https://node1.whyknot.dev/api/proxy?url=XYZ";
        string id = r.GetOrAddId(url);
        Assert.Equal(url, r.TryGetUrl(id));
    }

    [Fact]
    public void Registry_TryGetUrl_UnknownId_ReturnsNull()
    {
        var r = new SegmentIdRegistry();
        Assert.Null(r.TryGetUrl("000000000000"));
        Assert.Null(r.TryGetUrl(""));
        Assert.Null(r.TryGetUrl(null!));
    }

    [Fact]
    public void Registry_GetOrAddId_ConcurrentSameUrl_ReturnsConsistentId()
    {
        // Race-test: 64 threads call GetOrAddId with the same URL. They
        // should all return the same id, and the registry should hold
        // exactly one entry. Atomic dedup-by-URL via TryAdd + race rollback.
        var r = new SegmentIdRegistry();
        const string url = "https://node1.whyknot.dev/api/proxy?url=race";
        var results = new string[64];
        var threads = new List<Thread>();
        var ready = new ManualResetEventSlim(false);
        for (int i = 0; i < 64; i++)
        {
            int idx = i;
            var t = new Thread(() =>
            {
                ready.Wait();
                results[idx] = r.GetOrAddId(url);
            });
            t.Start();
            threads.Add(t);
        }
        ready.Set();
        foreach (var t in threads) t.Join();

        // Every thread saw the same id.
        var distinct = results.Distinct().ToList();
        Assert.Single(distinct);
        // Registry has exactly one entry.
        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void Registry_GetOrAddId_ConcurrentDistinctUrls_AllRegistered()
    {
        // 256 threads, each registering a distinct URL. Final registry
        // count is 256; every call returned a non-empty id; no two threads
        // saw the same id (collision protection on top of TryAdd).
        var r = new SegmentIdRegistry();
        const int n = 256;
        var results = new string[n];
        var threads = new List<Thread>();
        var ready = new ManualResetEventSlim(false);
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var t = new Thread(() =>
            {
                ready.Wait();
                results[idx] = r.GetOrAddId("https://example.com/" + idx);
            });
            t.Start();
            threads.Add(t);
        }
        ready.Set();
        foreach (var t in threads) t.Join();

        Assert.Equal(n, r.Count);
        Assert.All(results, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.Equal(n, results.Distinct().Count());
    }

    [Fact]
    public void Registry_Eviction_DropsOldestPastHardCap()
    {
        // Tiny caps for fast verification. Add 12 URLs into a registry capped
        // at hard=10/soft=5. After the 11th-and-beyond inserts, the oldest
        // entries should be evicted down to soft cap.
        var r = new SegmentIdRegistry(softCap: 5, hardCap: 10);
        var ids = new string[12];
        for (int i = 0; i < 12; i++)
            ids[i] = r.GetOrAddId("https://example.com/" + i);

        // After the 11th + 12th inserts each pushed past hard cap, the
        // registry should evict down to soft cap. Final count <= hard cap.
        Assert.True(r.Count <= 10, $"Expected count <= 10, got {r.Count}.");

        // The most-recently-added URL (index 11) is still present.
        Assert.Equal("https://example.com/11", r.TryGetUrl(ids[11]));

        // The earliest URLs (index 0, 1) should have been evicted.
        Assert.Null(r.TryGetUrl(ids[0]));
        Assert.Null(r.TryGetUrl(ids[1]));
    }

    [Fact]
    public void Registry_Constructor_ValidatesCaps()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new SegmentIdRegistry(softCap: 0, hardCap: 10));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new SegmentIdRegistry(softCap: 5, hardCap: 0));
        Assert.Throws<System.ArgumentException>(
            () => new SegmentIdRegistry(softCap: 100, hardCap: 50));
    }
}
