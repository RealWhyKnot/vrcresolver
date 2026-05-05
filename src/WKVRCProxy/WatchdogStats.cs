namespace WKVRCProxy;

// Process-wide counters surfaced by the periodic Heartbeat ticker. All
// counters are interlocked-incremented so increments from the IPC accept
// loop, the WS dispatch loop, and the run loop don't lose updates under
// concurrent resolves. Bytes counter caps at long.MaxValue (~9.2 EB --
// effectively unbounded for any conceivable session).
//
// Stats are session-scoped: reset to zero on each watchdog start. Not
// persisted across launches. No platform-specific APIs -- kept platform-
// agnostic so MeshClient (which isn't [SupportedOSPlatform("windows")])
// can call it without CA1416.
internal static class WatchdogStats
{
    public static readonly DateTime StartUtc = DateTime.UtcNow;

    private static long _resolvesTotal;
    private static long _resolvesViaLhYt;
    private static long _resolvesCacheHits;
    private static long _bytesEstimateTotal;
    private static long _reconnectCount;

    public static long ResolvesTotal => Interlocked.Read(ref _resolvesTotal);
    public static long ResolvesViaLhYt => Interlocked.Read(ref _resolvesViaLhYt);
    public static long ResolvesCacheHits => Interlocked.Read(ref _resolvesCacheHits);
    public static long BytesEstimateTotal => Interlocked.Read(ref _bytesEstimateTotal);
    public static long ReconnectCount => Interlocked.Read(ref _reconnectCount);

    public static void RecordResolve(bool viaLhYt)
    {
        Interlocked.Increment(ref _resolvesTotal);
        if (viaLhYt) Interlocked.Increment(ref _resolvesViaLhYt);
    }

    public static void RecordCacheHit()
    {
        Interlocked.Increment(ref _resolvesCacheHits);
    }

    public static void RecordBytesEstimate(long bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _bytesEstimateTotal, bytes);
    }

    public static void RecordReconnect()
    {
        Interlocked.Increment(ref _reconnectCount);
    }
}
