using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace WKVRCProxy;

// In-memory URL compaction table used by HlsManifestRewriter. Long upstream
// segment URLs become short stable IDs in the returned manifest, then the
// relay expands those IDs back to upstream URLs when AVPro requests them.
[SupportedOSPlatform("windows")]
internal sealed class SegmentIdRegistry
{
    public const int DefaultSoftCap = 100_000;
    public const int DefaultHardCap = 200_000;

    private const int IdHexLength = 12;
    private const int IdRandomBytes = IdHexLength / 2;
    private const int MaxGenerationAttempts = 8;

    internal sealed record SegmentEntry(string Url, string Ext);

    private readonly ConcurrentDictionary<string, SegmentEntry> _idToEntry = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _urlToId = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    private readonly int _softCap;
    private readonly int _hardCap;

    public SegmentIdRegistry()
        : this(DefaultSoftCap, DefaultHardCap) { }

    public SegmentIdRegistry(int softCap, int hardCap)
    {
        if (softCap <= 0 || hardCap <= 0)
            throw new ArgumentOutOfRangeException(softCap <= 0 ? nameof(softCap) : nameof(hardCap),
                "Caps must be positive.");
        if (hardCap < softCap)
            throw new ArgumentException("hardCap must be >= softCap.", nameof(hardCap));
        _softCap = softCap;
        _hardCap = hardCap;
    }

    public int Count => _idToEntry.Count;

    public string GetOrAddId(string url) => GetOrAddId(url, "");

    public string GetOrAddId(string url, string ext)
    {
        if (string.IsNullOrEmpty(url))
            throw new ArgumentException("URL must be non-empty.", nameof(url));
        ext ??= "";

        if (_urlToId.TryGetValue(url, out var existingId))
            return existingId;

        EvictIfOver();

        for (int attempts = 0; attempts < MaxGenerationAttempts; attempts++)
        {
            string newId = GenerateId();
            var entry = new SegmentEntry(url, ext);

            if (!_idToEntry.TryAdd(newId, entry))
                continue;

            if (_urlToId.TryAdd(url, newId))
            {
                _insertionOrder.Enqueue(newId);
                return newId;
            }

            _idToEntry.TryRemove(newId, out _);
            if (_urlToId.TryGetValue(url, out var concurrentId))
                return concurrentId;
        }

        throw new InvalidOperationException(
            "SegmentIdRegistry could not allocate a unique ID after "
            + MaxGenerationAttempts + " attempts. RNG broken or registry "
            + "wildly oversized; investigate before resuming.");
    }

    public string? TryGetUrl(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _idToEntry.TryGetValue(id, out var entry) ? entry.Url : null;
    }

    public SegmentEntry? TryGetEntry(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _idToEntry.TryGetValue(id, out var entry) ? entry : null;
    }

    private void EvictIfOver()
    {
        int over = _idToEntry.Count - _hardCap;
        if (over <= 0) return;

        int target = _idToEntry.Count - _softCap;
        for (int i = 0; i < target; i++)
        {
            if (!_insertionOrder.TryDequeue(out var oldId)) break;
            if (_idToEntry.TryRemove(oldId, out var entry))
                _urlToId.TryRemove(new KeyValuePair<string, string>(entry.Url, oldId));
        }
    }

    public void ClearForTesting()
    {
        _idToEntry.Clear();
        _urlToId.Clear();
        while (_insertionOrder.TryDequeue(out _)) { }
    }

    private static string GenerateId()
    {
        Span<byte> bytes = stackalloc byte[IdRandomBytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
