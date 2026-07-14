using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using MessagePack;
using MessagePack.Resolvers;
using VrcResolver.Shared;

namespace VrcResolver;

internal sealed partial class MeshClient : IAsyncDisposable
{
    // Fire-and-forget client → server feedback frame. Sent when VrcLogMonitor
    // observes that AVPro couldn't actually play a URL the dispatcher
    // returned (load_failure within 10 s of Opening, or silent_stall after
    // 12 s of nothing). Drops silently if the WS is down — the next launch
    // re-reads the current output_log_*.txt and reports any unsignalled
    // failures it finds there.
    //
    // Feature-gated on welcome.features containing "playback_feedback" so an
    // older server (before 2026.5.4.0-0AFF) doesn't see an unknown action.
    public async Task SendPlaybackFeedbackAsync(string url, string kind, int msSinceOpen, int? deliveredHeight = null)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(kind)) return;

        var features = _serverFeatures;
        if (features == null || Array.IndexOf(features, WireConstants.ActionPlaybackFeedback) < 0)
            return;

        string? cid = LookupRecentCorrelationId(url);

        byte[] payload;
        try
        {
            payload = BuildPlaybackFeedbackPayload(
                url, kind, msSinceOpen, _clientId, cid, DateTime.UtcNow, deliveredHeight);
        }
        catch { return; }

        try
        {
            await SendTextFrameAsync(payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* best-effort — heartbeat/run-loop will catch a dead socket */ }
    }

    // Frame builder split out so the wire shape can be unit-tested without
    // standing up a real MeshClient + WS. `correlation_id` is omitted (not
    // serialized as null) when caller passes null — keeps the frame shape
    // consistent with "missing field" semantics on the server.
    internal static byte[] BuildPlaybackFeedbackPayload(
        string url,
        string kind,
        int msSinceOpen,
        string clientId,
        string? correlationId,
        DateTime timestampUtc,
        int? deliveredHeight = null)
    {
        // AOT migration: Dictionary<string, object?> + reflection-based
        // SerializeToUtf8Bytes replaced with the typed PlaybackFeedbackFrame
        // DTO routed through MeshJsonContext source-gen. Wire shape
        // preserved byte-exact -- correlation_id still omitted (not
        // serialized as null) when caller passes null, matched by
        // [JsonIgnore(Condition = WhenWritingNull)] on the property.
        var frame = new PlaybackFeedbackFrame
        {
            Url = url,
            Kind = kind,
            Timestamp = timestampUtc.ToString("o"),
            MsSinceOpen = msSinceOpen,
            ClientId = clientId,
            CorrelationId = string.IsNullOrEmpty(correlationId) ? null : correlationId,
            DeliveredHeight = deliveredHeight is > 0 ? deliveredHeight : null,
        };
        return JsonSerializer.SerializeToUtf8Bytes(frame, MeshJsonContext.Default.PlaybackFeedbackFrame);
    }

    private string? LookupRecentCorrelationId(string url)
    {
        lock (_recentCidsLock)
        {
            if (!_recentCids.TryGetValue(url, out var entry))
                return null;
            if (DateTime.UtcNow - entry.At > RecentCidsTtl)
            {
                _recentCids.Remove(url);
                return null;
            }
            return entry.Cid;
        }
    }

    private void RememberResolvedUrlCid(string url, string cid)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(cid)) return;
        lock (_recentCidsLock)
        {
            _recentCids[url] = (cid, DateTime.UtcNow);
            if (_recentCids.Count <= MaxRecentCids) return;

            // Cap by evicting the oldest. 256 entries × occasional eviction is
            // cheap enough; no need for a proper LRU structure.
            string? oldestKey = null;
            DateTime oldestAt = DateTime.MaxValue;
            foreach (var kvp in _recentCids)
            {
                if (kvp.Value.At < oldestAt) { oldestAt = kvp.Value.At; oldestKey = kvp.Key; }
            }
            if (oldestKey != null) _recentCids.Remove(oldestKey);
        }
    }
}
