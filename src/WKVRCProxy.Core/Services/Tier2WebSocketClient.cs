using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class Tier2WebSocketClient : IProxyModule, IDisposable
{
    public string Name => "Tier2Client";
    private Logger? _logger;
    private SettingsManager? _settings;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly List<(string Id, string Url)> _nodes = new()
    {
        ("node1", "https://node1.whyknot.dev"),
        ("node2", "https://node2.whyknot.dev")
    };

    private string? _activeNodeId;
    private ClientWebSocket? _webSocket;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pendingRequests = new();
    private bool _isConnecting = false;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public string ActiveNode => _activeNodeId ?? "None";

    public Tier2WebSocketClient(Logger logger)
    {
        _logger = logger;
    }

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _settings = context.Settings;
        Task.Run(MaintainConnection);
        return Task.CompletedTask;
    }

    public void Shutdown()
    {
        _cts.Cancel();
        _webSocket?.Dispose();
    }

    private async Task MaintainConnection()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!IsConnected && !_isConnecting)
            {
                _logger?.Debug("Tier 2 not connected, attempting to connect to best node...");
                await ConnectToBestNodeAsync();
            }
            await Task.Delay(10000, _cts.Token);
        }
    }

    private async Task ConnectToBestNodeAsync()
    {
        _isConnecting = true;
        _logger?.Debug("[Tier 2] Searching for best node...");

        try
        {
            var results = await Task.WhenAll(_nodes.Select(async n =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var response = await _httpClient.GetAsync(n.Url + "/health", _cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return (Node: n, Latency: sw.ElapsedMilliseconds, Ok: true);
                    }
                }
                catch (Exception ex) { _logger?.Debug("Node " + n.Id + " health check failed: " + ex.Message); }
                return (Node: n, Latency: long.MaxValue, Ok: false);
            }));

            var best = results.Where(r => r.Ok).OrderBy(r => r.Latency).FirstOrDefault();

            if (best.Ok)
            {
                _logger?.Info("[Tier 2] Selected node: " + best.Node.Id + " (" + best.Latency + "ms)");
                await EstablishWebSocketAsync(best.Node.Id, best.Node.Url);
            }
            else
            {
                _logger?.Warning("[Tier 2] No nodes reachable.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("[Tier 2] Node selection error: " + ex.Message);
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private async Task EstablishWebSocketAsync(string id, string baseUrl)
    {
        try
        {
            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            string wsUrl = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/mesh";
            await _webSocket.ConnectAsync(new Uri(wsUrl), _cts.Token);
            
            _activeNodeId = id;
            _logger?.Success("[Tier 2] Connected to node " + id.ToUpper());
            
            _ = Task.Run(ReceiveLoop);
        }
        catch (Exception ex)
        {
            _logger?.Warning("[Tier 2] Failed to establish WebSocket to " + id + ": " + ex.Message);
        }
    }

    public event Action<string, object>? OnRelayMessage;

    private async Task ReceiveLoop()
    {
        var buffer = new byte[1024 * 16];
        try
        {
            while (IsConnected && !_cts.IsCancellationRequested)
            {
                var result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger?.Warning("[Tier 2] Connection closed by " + _activeNodeId?.ToUpper());
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                ProcessMessage(message);
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
                _logger?.Error("[Tier 2] WebSocket receive error: " + ex.Message);
        }
        finally
        {
            _activeNodeId = null;
            foreach (var tcs in _pendingRequests.Values) tcs.TrySetResult(null);
            _pendingRequests.Clear();
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("action", out var actionElem)) return;
            string action = actionElem.GetString() ?? "";

            if (action == "resolve_result")
            {
                string requestId = (root.TryGetProperty("id", out var idElem) ? idElem.GetString() : null) ?? "";
                string? streamUrl = root.TryGetProperty("stream_url", out var urlElem) ? urlElem.GetString() : null;
                bool success = root.TryGetProperty("success", out var successElem) && successElem.GetBoolean();
                
                if (!string.IsNullOrEmpty(requestId) && _pendingRequests.TryRemove(requestId, out var tcs))
                {
                    if (success && !string.IsNullOrEmpty(streamUrl))
                        _logger?.Debug("[Tier 2] resolve_result [" + requestId.Substring(0, 8) + "...]: success, URL length=" + streamUrl.Length);
                    else
                        _logger?.Warning("[Tier 2] resolve_result [" + requestId.Substring(0, 8) + "...]: " + (success ? "success flag set but URL is empty" : "node reported failure — no stream URL"));
                    tcs.TrySetResult(success ? streamUrl : null);
                }
                else if (!string.IsNullOrEmpty(requestId))
                {
                    _logger?.Warning("[Tier 2] resolve_result for unknown/already-expired request [" + requestId.Substring(0, Math.Min(8, requestId.Length)) + "...] — likely a timeout race.");
                }
            }
            else if (action == "relay_ready" || action == "relay_error" || action == "relay_read" || action == "resolve_log")
            {
                OnRelayMessage?.Invoke(action, root);
            }
        }
        catch (Exception ex) { _logger?.Warning("[Tier 2] Failed to parse message from " + _activeNodeId + ": " + ex.Message); }
    }

    public async Task SendMessageAsync(object message)
    {
        if (!IsConnected) return;
        try
        {
            string json = JsonSerializer.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception ex) { _logger?.Error("[Tier 2] Send error: " + ex.Message); }
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        if (!IsConnected) return;
        try
        {
            await _webSocket!.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, _cts.Token);
        }
        catch (Exception ex) { _logger?.Error("[Tier 2] Binary send error: " + ex.Message); }
    }

    public async Task<string?> ResolveUrlAsync(string url, string player, int maxHeight, string? correlationId = null, CancellationToken ct = default)
    {
        string shortUrl = url.Length > 100 ? url.Substring(0, 100) + "..." : url;
        string prefix = correlationId != null ? "[" + correlationId + "] [Tier 2] " : "[Tier 2] ";
        _logger?.Debug(prefix + "ResolveUrlAsync: player=" + player + " maxHeight=" + maxHeight + " url=" + shortUrl);

        if (!IsConnected)
        {
            _logger?.Warning(prefix + "Not connected — attempting reconnect before resolve.");
            await ConnectToBestNodeAsync();
            if (!IsConnected)
            {
                _logger?.Warning(prefix + "Reconnect failed — Tier 2 unavailable for this request.");
                return null;
            }
        }

        string requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<string?>();
        _pendingRequests[requestId] = tcs;

        var request = new
        {
            action = "resolve",
            url = url,
            player = player.ToLower(),
            maxHeight = maxHeight,
            id = requestId
        };

        try
        {
            string json = JsonSerializer.Serialize(request);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

            int timeoutSeconds = Math.Max(5, _settings?.Config.Tier2TimeoutSeconds ?? 60);
            // Linked CTS so the caller can cancel us (race winner found, shutdown) without
            // waiting out the full timeout. The Task.Delay is cancelled either by the caller
            // or by our own cleanup path below when tcs.Task completes first.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutTask = Task.Delay(timeoutSeconds * 1000, timeoutCts.Token);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completed == timeoutTask)
            {
                _pendingRequests.TryRemove(requestId, out _);
                if (ct.IsCancellationRequested)
                {
                    _logger?.Debug(prefix + "Resolution cancelled by caller for: " + shortUrl);
                }
                else
                {
                    _logger?.Warning(prefix + "Resolution timed out after " + timeoutSeconds + "s via " + _activeNodeId?.ToUpper() + " for: " + shortUrl);
                }
                return null;
            }

            // tcs.Task won — free the timer thread immediately.
            try { timeoutCts.Cancel(); } catch { }

            string? resolved = await tcs.Task;
            if (resolved != null)
                _logger?.Debug(prefix + "Resolved via " + _activeNodeId?.ToUpper() + ": " + (resolved.Length > 100 ? resolved.Substring(0, 100) + "..." : resolved));
            else
                _logger?.Warning(prefix + "Node " + _activeNodeId?.ToUpper() + " returned null for: " + shortUrl);
            return resolved;
        }
        catch (Exception ex)
        {
            _logger?.Error(prefix + "Request error for " + shortUrl + ": " + ex.Message);
            _pendingRequests.TryRemove(requestId, out _);
            return null;
        }
    }

    public ModuleHealthReport GetHealthReport()
    {
        if (IsConnected)
        {
            return new ModuleHealthReport
            {
                ModuleName = Name,
                Status = HealthStatus.Healthy,
                Reason = "",
                LastChecked = DateTime.Now
            };
        }
        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = HealthStatus.Degraded,
            Reason = _isConnecting
                ? "Connecting to cloud nodes..."
                : "No cloud nodes reachable -- Tier 2 resolution unavailable",
            LastChecked = DateTime.Now
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        _webSocket?.Dispose();
        _httpClient.Dispose();
        _cts.Dispose();
    }
}
