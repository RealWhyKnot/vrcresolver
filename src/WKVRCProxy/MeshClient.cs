using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Persistent reconnecting WebSocket client to whyknot.dev's mesh endpoint.
//
// Apex-302 discovery: GET https://whyknot.dev/ with auto-redirect off, parse
// Location for the assigned nodeN.whyknot.dev hostname. Cached in memory only;
// re-resolved if reconnect attempts keep failing for more than 5 min straight.
//
// Public surface is ResolveAsync — pipe-side handlers call this and convert
// the returned JsonDocument into a wire frame for the patched yt-dlp.
internal sealed class MeshClient : IAsyncDisposable
{
    private static readonly Uri ApexUrl = new("https://whyknot.dev/");
    private static readonly TimeSpan ApexAttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PongDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ApexReResolveAfter = TimeSpan.FromMinutes(5);
    private static readonly int[] ReconnectCapsSec = { 1, 2, 4, 8, 16, 30 };

    private readonly string _userAgent;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly Random _rng = new();

    private ClientWebSocket? _ws;
    private string? _cachedNodeHost;
    private CancellationTokenSource? _runCts;
    private Task? _runner;
    private DateTime _firstReconnectFailureUtc = DateTime.MinValue;
    private DateTime _lastPongUtc = DateTime.MinValue;
    private int _reconnectAttempt;
    private bool _wasConnected;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public MeshClient()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        _userAgent = "WKVRCProxy-Watchdog/" + ver;
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _httpClient = new HttpClient(handler) { Timeout = ApexAttemptTimeout };
    }

    public Task StartAsync()
    {
        _runCts = new CancellationTokenSource();
        _runner = Task.Run(() => RunLoopAsync(_runCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _runCts?.Cancel();
        FailAllPending(WireConstants.FallbackServerUnreachable);
        try
        {
            if (_ws?.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(2000);
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", cts.Token).ConfigureAwait(false);
            }
        }
        catch { /* best-effort */ }
        if (_runner != null)
        {
            try { await _runner.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    public async Task<JsonDocument> ResolveAsync(string url, string? player, int? maxHeight, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
            return MakeFallbackDoc("", WireConstants.FallbackServerUnreachable);

        string id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var req = new ResolveRequest { Id = id, Url = url, Player = player, MaxHeight = maxHeight };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(req);

        try
        {
            await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            return MakeFallbackDoc(id, WireConstants.FallbackServerUnreachable);
        }

        await using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetCanceled();
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return MakeFallbackDoc(id, WireConstants.FallbackServerUnreachable);
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string node = await ResolveNodeHostAsync(ct).ConfigureAwait(false);
                _cachedNodeHost = node;

                _ws = new ClientWebSocket();
                _ws.Options.SetRequestHeader("User-Agent", _userAgent);
                var wsUri = new Uri("wss://" + node + "/mesh");
                await _ws.ConnectAsync(wsUri, ct).ConfigureAwait(false);

                _wasConnected = true;
                _firstReconnectFailureUtc = DateTime.MinValue;
                _lastPongUtc = DateTime.UtcNow;
                Console.WriteLine("[mesh] connected");

                await PumpAsync(ct).ConfigureAwait(false);
                Console.WriteLine("[mesh] disconnected — clean close");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_wasConnected) Console.WriteLine("[mesh] disconnected — " + ex.Message);
                else if (_reconnectAttempt == 0) Console.WriteLine("[mesh] disconnected — " + ex.Message);
                _wasConnected = false;
                FailAllPending(WireConstants.FallbackServerUnreachable);
                if (_firstReconnectFailureUtc == DateTime.MinValue)
                    _firstReconnectFailureUtc = DateTime.UtcNow;
                if (DateTime.UtcNow - _firstReconnectFailureUtc > ApexReResolveAfter)
                    _cachedNodeHost = null;
            }
            finally
            {
                try { _ws?.Dispose(); } catch { /* ignore */ }
                _ws = null;
            }

            if (ct.IsCancellationRequested) break;

            _reconnectAttempt++;
            int capSec = ReconnectCapsSec[Math.Min(_reconnectAttempt - 1, ReconnectCapsSec.Length - 1)];
            int waitMs = _rng.Next(0, capSec * 1000 + 1);
            Console.WriteLine($"[mesh] reconnect attempt {_reconnectAttempt} in {waitMs / 1000} s");
            try { await Task.Delay(waitMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<string> ResolveNodeHostAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedNodeHost)) return _cachedNodeHost!;

        Exception? lastEx = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApexUrl);
                req.Headers.UserAgent.ParseAdd(_userAgent);
                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400 && resp.Headers.Location != null)
                {
                    var loc = resp.Headers.Location;
                    string host = loc.IsAbsoluteUri ? loc.Host : loc.OriginalString;
                    if (!string.IsNullOrEmpty(host)) return host;
                }
                lastEx = new InvalidOperationException("apex returned " + (int)resp.StatusCode + " with no usable Location");
            }
            catch (Exception ex) { lastEx = ex; }
            try { await Task.Delay(1000 * attempt, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        FailAllPending(WireConstants.FallbackServerUnreachable);
        Console.WriteLine("[mesh] apex discovery failed — falling back to native yt-dlp until reconnect succeeds.");
        throw lastEx ?? new InvalidOperationException("apex discovery failed");
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task hbTask = HeartbeatLoopAsync(pumpCts.Token);

        try
        {
            var buf = new Memory<byte>(new byte[64 * 1024]);
            using var ms = new MemoryStream();
            while (!pumpCts.IsCancellationRequested)
            {
                ms.SetLength(0);
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await _ws!.ReceiveAsync(buf, pumpCts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf.Span[..r.Count]);
                    if (ms.Length > 4 * 1024 * 1024) throw new InvalidOperationException("frame too large");
                } while (!r.EndOfMessage);

                if (r.MessageType != WebSocketMessageType.Text) continue;
                await DispatchFrameAsync(ms.ToArray(), pumpCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            pumpCts.Cancel();
            try { await hbTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (_ws?.State != WebSocketState.Open) return;
            DateTime sentAt = DateTime.UtcNow;
            try
            {
                var ping = JsonSerializer.SerializeToUtf8Bytes(new { action = WireConstants.ActionPing });
                await _ws.SendAsync(ping, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch { return; }

            try { await Task.Delay(PongDeadline, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (_lastPongUtc < sentAt)
            {
                try { _ws?.Abort(); } catch { /* ignore */ }
                return;
            }
        }
    }

    private async Task DispatchFrameAsync(byte[] payload, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch { return; }

        string action = "";
        if (doc.RootElement.TryGetProperty("action", out var actionEl) && actionEl.ValueKind == JsonValueKind.String)
            action = actionEl.GetString() ?? "";

        switch (action)
        {
            case WireConstants.ActionResolved:
            case WireConstants.ActionFallbackNative:
            {
                string id = "";
                if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    id = idEl.GetString() ?? "";
                if (string.IsNullOrEmpty(id)) { doc.Dispose(); return; }
                if (_pending.TryRemove(id, out var tcs))
                {
                    if (action == WireConstants.ActionResolved) _reconnectAttempt = 0;
                    tcs.TrySetResult(doc);
                }
                else
                {
                    doc.Dispose();
                }
                return;
            }
            case WireConstants.ActionResolveLog:
                doc.Dispose();
                return;
            case WireConstants.ActionPong:
                _lastPongUtc = DateTime.UtcNow;
                doc.Dispose();
                return;
            case WireConstants.ActionPing:
                doc.Dispose();
                try
                {
                    var pong = JsonSerializer.SerializeToUtf8Bytes(new { action = WireConstants.ActionPong });
                    await _ws!.SendAsync(pong, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
                catch { /* heartbeat will catch dead socket */ }
                return;
            default:
                Console.WriteLine("[mesh] unknown action — discarding: " + action);
                doc.Dispose();
                return;
        }
    }

    private void FailAllPending(string reason)
    {
        foreach (var kvp in _pending.ToArray())
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                try { tcs.TrySetResult(MakeFallbackDoc(kvp.Key, reason)); }
                catch { tcs.TrySetCanceled(); }
            }
        }
    }

    private static JsonDocument MakeFallbackDoc(string id, string reason)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = WireConstants.ActionFallbackNative,
            id,
            reason
        });
        return JsonDocument.Parse(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _httpClient.Dispose();
        _runCts?.Dispose();
    }
}
