using WKVRCProxy.Shared;

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
    private static long _relayRequestsTotal;
    private static long _relayBytesTotal;
    private static long _whyKnotRelayRequestsTotal;
    private static long _whyKnotRelayBytesTotal;
    private static long _lastRelayTicksUtc;
    private static long _lastWhyKnotRelayTicksUtc;

    public static long ResolvesTotal => Interlocked.Read(ref _resolvesTotal);
    public static long ResolvesViaLhYt => Interlocked.Read(ref _resolvesViaLhYt);
    public static long ResolvesCacheHits => Interlocked.Read(ref _resolvesCacheHits);
    public static long BytesEstimateTotal => Interlocked.Read(ref _bytesEstimateTotal);
    public static long ReconnectCount => Interlocked.Read(ref _reconnectCount);
    public static long RelayRequestsTotal => Interlocked.Read(ref _relayRequestsTotal);
    public static long RelayBytesTotal => Interlocked.Read(ref _relayBytesTotal);
    public static long WhyKnotRelayRequestsTotal => Interlocked.Read(ref _whyKnotRelayRequestsTotal);
    public static long WhyKnotRelayBytesTotal => Interlocked.Read(ref _whyKnotRelayBytesTotal);

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

    public static void RecordRelayRequest(string targetUrl)
    {
        Interlocked.Increment(ref _relayRequestsTotal);
        if (IsWhyKnotTarget(targetUrl))
            Interlocked.Increment(ref _whyKnotRelayRequestsTotal);
    }

    public static void RecordRelayBytes(string targetUrl, long bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _relayBytesTotal, bytes);
        if (IsWhyKnotTarget(targetUrl))
            Interlocked.Add(ref _whyKnotRelayBytesTotal, bytes);
        TouchRelay(targetUrl, bytes);
    }

    public static WatchdogActivitySnapshot GetActivitySnapshot()
    {
        return new WatchdogActivitySnapshot(
            ResolvesTotal,
            ResolvesViaLhYt,
            ResolvesCacheHits,
            BytesEstimateTotal,
            ReconnectCount,
            RelayRequestsTotal,
            RelayBytesTotal,
            WhyKnotRelayRequestsTotal,
            WhyKnotRelayBytesTotal,
            TicksToUtc(Interlocked.Read(ref _lastRelayTicksUtc)),
            TicksToUtc(Interlocked.Read(ref _lastWhyKnotRelayTicksUtc)));
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _resolvesTotal, 0);
        Interlocked.Exchange(ref _resolvesViaLhYt, 0);
        Interlocked.Exchange(ref _resolvesCacheHits, 0);
        Interlocked.Exchange(ref _bytesEstimateTotal, 0);
        Interlocked.Exchange(ref _reconnectCount, 0);
        Interlocked.Exchange(ref _relayRequestsTotal, 0);
        Interlocked.Exchange(ref _relayBytesTotal, 0);
        Interlocked.Exchange(ref _whyKnotRelayRequestsTotal, 0);
        Interlocked.Exchange(ref _whyKnotRelayBytesTotal, 0);
        Interlocked.Exchange(ref _lastRelayTicksUtc, 0);
        Interlocked.Exchange(ref _lastWhyKnotRelayTicksUtc, 0);
    }

    private static void TouchRelay(string targetUrl, long bytes)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _lastRelayTicksUtc, nowTicks);
        if (bytes > 0 && IsWhyKnotTarget(targetUrl))
            Interlocked.Exchange(ref _lastWhyKnotRelayTicksUtc, nowTicks);
    }

    private static bool IsWhyKnotTarget(string targetUrl)
    {
        return Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)
            && WhyKnotUrlPolicy.IsWhyKnotHost(uri.Host);
    }

    private static DateTime? TicksToUtc(long ticks)
    {
        return ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }
}

internal readonly record struct WatchdogActivitySnapshot(
    long ResolvesTotal,
    long ResolvesViaLhYt,
    long ResolvesCacheHits,
    long BytesEstimateTotal,
    long ReconnectCount,
    long RelayRequestsTotal,
    long RelayBytesTotal,
    long WhyKnotRelayRequestsTotal,
    long WhyKnotRelayBytesTotal,
    DateTime? LastRelayUtc,
    DateTime? LastWhyKnotRelayUtc)
{
    public bool RelayActive(DateTime nowUtc, TimeSpan window)
    {
        return LastRelayUtc.HasValue && nowUtc - LastRelayUtc.Value <= window;
    }

    public bool WhyKnotActive(DateTime nowUtc, TimeSpan window)
    {
        return LastWhyKnotRelayUtc.HasValue && nowUtc - LastWhyKnotRelayUtc.Value <= window;
    }
}
