using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;

[SupportedOSPlatform("windows")]
[DependsOn(typeof(RelayPortManager), critical: true)]
[DependsOn(typeof(ProxyRuleManager))]
[DependsOn(typeof(CurlImpersonateClient))]
[DependsOn(typeof(PotProviderService))]
[DependsOn(typeof(BrowserSessionCache))]
public class RelayServer : IProxyModule, IDisposable
{
    public string Name => "RelayServer";
    public event Action<RelayEvent>? OnRelayEvent;

    // Fires when a downstream client (AVPro/Unity) closes the relay connection mid-write with
    // fewer than AbortBytesThreshold bytes served. This is the "the player can't parse this
    // format" signal — legitimate seeks happen after the player has already received hundreds of
    // KB of payload, so a sub-256KB abort is almost always a format rejection.
    //
    // ResolutionEngine subscribes, counts aborts per target URL in a rolling window, and when
    // N aborts hit inside the window, triggers the same PlaybackFailed demotion path used for
    // AVPro "Loading failed" events. This is what catches Unity-player failures, which don't
    // emit the AVPro-style log line.
    public event Action<string /*targetUrl*/, long /*bytesAtAbort*/>? OnClientAbortedEarly;
    private const long AbortBytesThreshold = 256 * 1024; // 256 KB — below this = format rejection

    private Logger? _logger;
    private IModuleContext? _context;
    private HttpListener? _listener;
    private CancellationTokenSource _cts = new();
    private RelayPortManager? _portManager;
    private ProxyRuleManager? _ruleManager;
    private CurlImpersonateClient? _curlClient;
    private PotProviderService? _potProvider;
    private BrowserSessionCache? _browserSessionCache;
    private SystemEventBus? _eventBus;
    private readonly HttpClient _httpClient = new HttpClient(
        new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }
    ) { Timeout = Timeout.InfiniteTimeSpan };

    // Suppress repeated "Relaying:" INFO lines for the same target URL within a sliding window.
    // Without this, a single playing video floods the log with 20+ identical lines per second
    // as AVPro re-polls the manifest and fetches segments through the same proxy URL.
    private readonly ConcurrentDictionary<string, DateTime> _recentlyLoggedTargets = new();
    private static readonly TimeSpan LogDedupeWindow = TimeSpan.FromSeconds(30);

    private bool ShouldLogRelay(string targetUrl)
    {
        var now = DateTime.UtcNow;
        if (_recentlyLoggedTargets.TryGetValue(targetUrl, out var lastLogged) && now - lastLogged < LogDedupeWindow)
            return false;
        _recentlyLoggedTargets[targetUrl] = now;
        // Opportunistic cleanup — keep dict from growing unboundedly over long sessions.
        if (_recentlyLoggedTargets.Count > 256)
        {
            var cutoff = now - LogDedupeWindow;
            foreach (var kv in _recentlyLoggedTargets)
                if (kv.Value < cutoff) _recentlyLoggedTargets.TryRemove(kv.Key, out _);
        }
        return true;
    }

    public Task InitializeAsync(IModuleContext context)
    {
        _context = context;
        _logger = context.Logger;

        try
        {
            _portManager = context.GetModule<RelayPortManager>();
            _ruleManager = context.GetModule<ProxyRuleManager>();
            _curlClient = context.GetModule<CurlImpersonateClient>();
            _potProvider = context.GetModule<PotProviderService>();
            try { _browserSessionCache = context.GetModule<BrowserSessionCache>(); } catch { /* optional — may not be registered if browser-extract disabled */ }
            _eventBus = context.EventBus;

            int attempts = 0;
            bool success = false;

            while (attempts < 5 && !success)
            {
                int port = _portManager.CurrentPort;

                if (port == 0)
                {
                    _logger.Error("RelayServer failed: Port is 0. Is RelayPortManager registered?");
                    return Task.CompletedTask;
                }

                _listener = new HttpListener();
                string prefix = "http://127.0.0.1:" + port + "/";
                _listener.Prefixes.Add(prefix);

                try
                {
                    _listener.Start();
                    success = true;
                    _logger.Success("Relay listening on port " + port);
                }
                catch (HttpListenerException)
                {
                    attempts++;
                    _logger.Warning("Port " + port + " conflict detected. Retrying... (" + attempts + "/5)");
                    _listener.Close();

                    if (attempts < 5)
                    {
                        _portManager.RefreshPort();
                    }
                }
            }

            if (!success)
            {
                _logger.Fatal("Unable to bind to any local port after 5 attempts. Please check your firewall or restart your PC.");
                _eventBus?.PublishError("RelayServer", new ErrorContext {
                    Category = ErrorCategory.Network,
                    Code = ErrorCodes.RELAY_PORT_BIND_FAILED,
                    Summary = "Relay server could not bind to any port",
                    Detail = "5 consecutive port binding attempts failed",
                    ActionHint = "Check your firewall settings or restart your PC",
                    IsRecoverable = false
                });
                return Task.CompletedTask;
            }

            _ = Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            _logger.Error("RelayServer Init Error: " + ex.Message, ex);
        }

        return Task.CompletedTask;
    }

    private async Task ListenLoop()
    {
        if (_listener == null) return;

        while (_listener.IsListening && !_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException) { /* Ignored on closing */ }
            catch (ObjectDisposedException) { /* Ignored on shutdown */ }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    _logger?.Error("Relay Listen Loop Error: " + ex.Message, ex);
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        Stream? sourceStream = null;
        HttpResponseMessage? response = null;
        try
        {
            if (!context.Request.Url!.AbsolutePath.StartsWith("/play", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            string? targetBase64 = context.Request.QueryString["target"];
            if (string.IsNullOrEmpty(targetBase64))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            // Re-replace spaces back with pluses for base64 safety (URL encoding converts + to space)
            targetBase64 = targetBase64.Replace(" ", "+");
            string targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(targetBase64));

            var ctx = RequestContext.Create(targetUrl);
            // Truncate URLs in logs to keep them readable
            string shortUrl = targetUrl.Length > 120 ? targetUrl.Substring(0, 120) + "..." : targetUrl;

            // Only log the first relay of a given URL within the dedupe window. Manifests re-polled
            // every few seconds and segment GETs that reuse /api/proxy?url= would otherwise flood
            // the log. Upstream errors and manifest parse failures are still logged unconditionally below.
            if (ShouldLogRelay(targetUrl))
                _logger?.Info("[" + ctx.CorrelationId + "] Relaying: " + shortUrl);

            var relayEvent = new RelayEvent {
                TargetUrl = targetUrl,
                Method = context.Request.HttpMethod,
                StatusCode = 0,
                BytesTransferred = 0,
                CorrelationId = ctx.CorrelationId
            };
            OnRelayEvent?.Invoke(relayEvent);
            _eventBus?.PublishRelay(relayEvent, ctx.CorrelationId);

            // Detect HLS content early from the URL — refined later from Content-Type header.
            // HLS manifests must be rewritten so all segment URLs route through the relay,
            // and ContentLength must NOT be forwarded (live manifests change size between polls).
            bool isHls = targetUrl.Contains(".m3u8") || targetUrl.Contains("m3u8");

            var uri = new Uri(targetUrl);
            ProxyRule rule = _ruleManager?.GetRuleForDomain(uri.Host) ?? new ProxyRule();

            if (rule.UsePoTokenProvider && _potProvider != null)
            {
                string? videoId = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("id") ?? "unknown";
                string? visitorData = "dummy-visitor-data";
                string? token = await _potProvider.GetPotTokenAsync(visitorData, videoId);
                if (!string.IsNullOrEmpty(token))
                {
                    targetUrl += (targetUrl.Contains("?") ? "&" : "?") + "pot=" + token;
                }
            }

            using var outboundRequest = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), targetUrl);

            foreach (string? key in context.Request.Headers.AllKeys)
            {
                if (key != null && rule.ForwardHeaders.Any(h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase)))
                {
                    outboundRequest.Headers.TryAddWithoutValidation(key, context.Request.Headers[key]);
                }
            }

            // UA precedence: explicit rule override > client-forwarded UA > Mozilla fallback.
            // Preserving the client UA matters for "VRChat movie world" hosts (vr-m.net etc.) that
            // only serve clients whose UA identifies as AVPro/UnityPlayer.
            if (!string.IsNullOrEmpty(rule.OverrideUserAgent))
            {
                outboundRequest.Headers.Remove("User-Agent");
                outboundRequest.Headers.UserAgent.ParseAdd(rule.OverrideUserAgent);
            }
            else if (!outboundRequest.Headers.TryGetValues("User-Agent", out _))
            {
                outboundRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            }

            if (rule.ForwardReferer == "always")
            {
                if (context.Request.Headers["Referer"] != null)
                    outboundRequest.Headers.Referrer = new Uri(context.Request.Headers["Referer"]!);
            }
            else if (rule.ForwardReferer == "never")
            {
                outboundRequest.Headers.Remove("Referer");
            }
            else if (rule.ForwardReferer == "same-origin" && context.Request.Headers["Referer"] != null)
            {
                var refUri = new Uri(context.Request.Headers["Referer"]!);
                if (refUri.Host.EndsWith(uri.Host) || uri.Host.EndsWith(refUri.Host))
                    outboundRequest.Headers.Referrer = refUri;
            }

            // Apply per-rule static header injections. Empty string value = strip the header. Any
            // other value replaces whatever was previously forwarded / set by UA/Referer logic.
            // Content-* headers go on the content (if any); everything else on the request.
            foreach (var kvp in rule.InjectHeaders)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                outboundRequest.Headers.Remove(kvp.Key);
                if (!string.IsNullOrEmpty(kvp.Value))
                {
                    if (!outboundRequest.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value))
                    {
                        // Some headers (Content-Type etc.) belong on HttpContent, not the request.
                        // Log at debug and skip; rare enough that we don't need a full content-builder here.
                        _logger?.Debug("[Relay] Skipped injecting header '" + kvp.Key + "' — not a valid request header (content header?).");
                    }
                }
            }

            // Browser-session replay: if BrowserExtractService captured a session for this host, replay
            // its exact cookies and headers on every outbound request. Captured headers take precedence
            // over rule-injected ones (browser is the source of truth for fingerprint-gated origins).
            // Keyed by bare host so the session survives manifest-path changes.
            string relayHost = BrowserSessionCache.HostFromUrl(targetUrl);
            BrowserSession? session = _browserSessionCache?.Get(relayHost);
            if (session != null)
            {
                foreach (var kvp in session.Headers)
                {
                    if (string.IsNullOrEmpty(kvp.Key)) continue;
                    outboundRequest.Headers.Remove(kvp.Key);
                    if (!outboundRequest.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value))
                        _logger?.Debug("[" + ctx.CorrelationId + "] [Relay] Session header '" + kvp.Key + "' could not be added to request (content header?).");
                }
                if (!string.IsNullOrEmpty(session.CookieHeader))
                {
                    outboundRequest.Headers.Remove("Cookie");
                    outboundRequest.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
                }
                _logger?.Debug("[" + ctx.CorrelationId + "] [Relay] Applied captured browser session for " + relayHost + " (" + session.Headers.Count + " headers, cookie " + (string.IsNullOrEmpty(session.CookieHeader) ? "empty" : session.CookieHeader.Length + " chars") + ").");
            }

            if (rule.UseCurlImpersonate && _curlClient != null && _curlClient.IsAvailable)
            {
                var dict = new Dictionary<string, string>();
                foreach (var h in outboundRequest.Headers)
                {
                    dict[h.Key] = string.Join(", ", h.Value);
                }
                sourceStream = await _curlClient.SendRequestAsync(context.Request.HttpMethod, targetUrl, dict);

                // Parse HTTP headers from curl's -i output (headers then blank line then body)
                var headerLines = new List<string>();
                var currentLine = new StringBuilder();
                int emptyLineCount = 0;

                while (true)
                {
                    byte[] b = new byte[1];
                    int r = await sourceStream.ReadAsync(b, 0, 1, _cts.Token);
                    if (r == 0) break;

                    if (b[0] == '\r') continue;

                    if (b[0] == '\n')
                    {
                        if (currentLine.Length == 0)
                        {
                            emptyLineCount++;
                            if (emptyLineCount >= 1) break;
                        }
                        else
                        {
                            headerLines.Add(currentLine.ToString());
                            currentLine.Clear();
                            emptyLineCount = 0;
                        }
                    }
                    else
                    {
                        currentLine.Append((char)b[0]);
                        emptyLineCount = 0;
                    }
                }

                if (headerLines.Count > 0 && headerLines[0].StartsWith("HTTP/"))
                {
                    var parts = headerLines[0].Split(' ', 3);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int sc))
                    {
                        context.Response.StatusCode = sc;
                        // Mirror the HttpClient-path invalidation: 401/403/429 = browser session stale.
                        if ((sc == 401 || sc == 403 || sc == 429) && session != null && _browserSessionCache != null)
                        {
                            _browserSessionCache.Invalidate(relayHost);
                            _logger?.Info("[" + ctx.CorrelationId + "] [Relay] Invalidated browser session for " + relayHost + " after upstream " + sc + " (curl path).");
                        }
                    }
                    headerLines.RemoveAt(0);
                }

                bool curlContentTypeIsHls = false;
                bool curlContentTypeObserved = false;
                long? curlContentLength = null;

                foreach (var headerLine in headerLines)
                {
                    int idx = headerLine.IndexOf(':');
                    if (idx > 0)
                    {
                        string key = headerLine.Substring(0, idx).Trim();
                        string value = headerLine.Substring(idx + 1).Trim();

                        if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;

                        if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            curlContentTypeObserved = true;
                            curlContentTypeIsHls = value.Contains("mpegurl") || value.Contains("m3u8");
                            if (curlContentTypeIsHls) isHls = true;
                        }

                        if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        {
                            if (long.TryParse(value, out long len)) curlContentLength = len;
                            continue;
                        }

                        try { context.Response.Headers.Add(key, value); } catch { }
                    }
                }

                // If Content-Type was present and explicitly says non-HLS, override the URL heuristic.
                // This prevents binary segment data being treated as a manifest and corrupted.
                if (curlContentTypeObserved && !curlContentTypeIsHls) isHls = false;
                // Set Content-Length now that isHls is finalised (skipped for HLS — set from manifest size below).
                if (!isHls && curlContentLength.HasValue) context.Response.ContentLength64 = curlContentLength.Value;

                // For HLS via curl: read sourceStream as text, rewrite, serve rewritten bytes
                if (isHls)
                {
                    using var reader = new StreamReader(sourceStream, Encoding.UTF8);
                    string rawManifest = await reader.ReadToEndAsync();
                    string rewritten = HlsManifestRewriter.Rewrite(rawManifest, targetUrl, _portManager!.CurrentPort, _logger);
                    byte[] manifestBytes = Encoding.UTF8.GetBytes(rewritten);
                    context.Response.ContentLength64 = manifestBytes.Length;
                    try { context.Response.Headers["Content-Type"] = "application/vnd.apple.mpegurl"; } catch { }
                    try
                    {
                        await context.Response.OutputStream.WriteAsync(manifestBytes, 0, manifestBytes.Length, _cts.Token);
                    }
                    catch (HttpListenerException)
                    {
                        // Player closed connection while reading the manifest — typically means
                        // the player parsed the manifest header (or its Content-Type) and
                        // decided it's unplayable. Almost certainly a Unity player refusing HLS.
                        try { OnClientAbortedEarly?.Invoke(targetUrl, 0); } catch { }
                    }
                    catch (IOException) { try { OnClientAbortedEarly?.Invoke(targetUrl, 0); } catch { } }
                    catch (TaskCanceledException) { /* shutdown */ }
                    catch (OperationCanceledException) { /* shutdown */ }
                    relayEvent.StatusCode = context.Response.StatusCode;
                    relayEvent.BytesTransferred = manifestBytes.Length;
                    OnRelayEvent?.Invoke(relayEvent);
                    return;
                }

                // Non-HLS curl path: binary stream copy
            }
            else
            {
                response = await _httpClient.SendAsync(outboundRequest, HttpCompletionOption.ResponseHeadersRead, _cts.Token);

                int upstreamStatus = (int)response.StatusCode;
                context.Response.StatusCode = upstreamStatus;
                // 2xx responses are routine; only surface non-success statuses (handled below as warnings).

                // If upstream returned an error, pass it through without HLS rewriting
                if (upstreamStatus >= 400)
                {
                    _logger?.Warning("[" + ctx.CorrelationId + "] Upstream error " + upstreamStatus + " for " + shortUrl);
                    // 401/403/429 = captured browser session no longer valid (cookie expired, IP flagged).
                    // Invalidate so the next resolve re-runs the browser and captures a fresh session.
                    if ((upstreamStatus == 401 || upstreamStatus == 403 || upstreamStatus == 429) && session != null && _browserSessionCache != null)
                    {
                        _browserSessionCache.Invalidate(relayHost);
                        _logger?.Info("[" + ctx.CorrelationId + "] [Relay] Invalidated browser session for " + relayHost + " after upstream " + upstreamStatus + ".");
                    }
                    string errorBody = await response.Content.ReadAsStringAsync();
                    byte[] errorBytes = Encoding.UTF8.GetBytes(errorBody);
                    context.Response.ContentLength64 = errorBytes.Length;
                    await context.Response.OutputStream.WriteAsync(errorBytes, 0, errorBytes.Length, _cts.Token);
                    relayEvent.StatusCode = upstreamStatus;
                    OnRelayEvent?.Invoke(relayEvent);
                    return;
                }

                bool httpContentTypeIsHls = false;
                bool httpContentTypeObserved = false;
                long? httpContentLength = null;

                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    string key = header.Key;
                    string value = string.Join(", ", header.Value);

                    if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;

                    if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        httpContentTypeObserved = true;
                        httpContentTypeIsHls = value.Contains("mpegurl") || value.Contains("m3u8");
                        if (httpContentTypeIsHls) isHls = true;
                    }

                    if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        if (long.TryParse(value, out long len)) httpContentLength = len;
                        continue;
                    }

                    try { context.Response.Headers.Add(key, value); } catch { }
                }

                // If Content-Type was present and explicitly says non-HLS, override the URL heuristic.
                // This prevents binary segment data being treated as a manifest and corrupted.
                if (httpContentTypeObserved && !httpContentTypeIsHls) isHls = false;
                // Set Content-Length now that isHls is finalised (skipped for HLS — set from manifest size below).
                if (!isHls && httpContentLength.HasValue) context.Response.ContentLength64 = httpContentLength.Value;

                // For HLS responses: read manifest as text, rewrite all segment URLs through relay,
                // then serve the rewritten content with correct Content-Length.
                if (isHls)
                {
                    string rawManifest = await response.Content.ReadAsStringAsync();
                    bool looksValid = rawManifest.TrimStart().StartsWith("#EXTM3U");
                    if (!looksValid)
                        _logger?.Warning("[" + ctx.CorrelationId + "] HLS manifest does not start with #EXTM3U — may be compressed or corrupt (first 100 bytes: " + rawManifest.Substring(0, Math.Min(100, rawManifest.Length)) + ")");
                    string rewritten = HlsManifestRewriter.Rewrite(rawManifest, targetUrl, _portManager!.CurrentPort, _logger);
                    byte[] manifestBytes = Encoding.UTF8.GetBytes(rewritten);
                    context.Response.ContentLength64 = manifestBytes.Length;
                    try { context.Response.Headers["Content-Type"] = "application/vnd.apple.mpegurl"; } catch { }
                    try
                    {
                        await context.Response.OutputStream.WriteAsync(manifestBytes, 0, manifestBytes.Length, _cts.Token);
                    }
                    catch (HttpListenerException) { try { OnClientAbortedEarly?.Invoke(targetUrl, 0); } catch { } }
                    catch (IOException) { try { OnClientAbortedEarly?.Invoke(targetUrl, 0); } catch { } }
                    catch (TaskCanceledException) { /* shutdown */ }
                    catch (OperationCanceledException) { /* shutdown */ }
                    relayEvent.StatusCode = context.Response.StatusCode;
                    relayEvent.BytesTransferred = manifestBytes.Length;
                    OnRelayEvent?.Invoke(relayEvent);
                    return;
                }

                sourceStream = await response.Content.ReadAsStreamAsync();
            }

            // Non-HLS binary streaming path: fire one final event with status + bytes once done.
            // A HttpListenerException here means the player closed the connection mid-stream.
            // Legit seeks happen after substantial playback (hundreds of KB), while format
            // rejections abort within the first few KB as the player reads enough to decide
            // "I can't play this." We fire OnClientAbortedEarly only below AbortBytesThreshold.
            bool clientAbortedShort = false;
            try
            {
                byte[] buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await sourceStream!.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
                {
                    await context.Response.OutputStream.WriteAsync(buffer, 0, bytesRead, _cts.Token);
                    relayEvent.BytesTransferred += bytesRead;
                }
            }
            catch (HttpListenerException) { clientAbortedShort = relayEvent.BytesTransferred < AbortBytesThreshold; }
            catch (IOException) { clientAbortedShort = relayEvent.BytesTransferred < AbortBytesThreshold; }
            catch (TaskCanceledException) { /* System shutting down */ }
            catch (OperationCanceledException) { /* System shutting down */ }

            if (clientAbortedShort)
            {
                try { OnClientAbortedEarly?.Invoke(targetUrl, relayEvent.BytesTransferred); } catch { }
            }

            relayEvent.StatusCode = context.Response.StatusCode;
            OnRelayEvent?.Invoke(relayEvent);
        }
        catch (Exception ex)
        {
            _logger?.Error("Relay Request Handling Error: " + ex.Message, ex);
            try { context.Response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { sourceStream?.Dispose(); } catch { }
            try { response?.Dispose(); } catch { }
            try { context.Response.Close(); } catch { }
        }
    }

    public ModuleHealthReport GetHealthReport()
    {
        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = (_listener != null && _listener.IsListening)
                ? HealthStatus.Healthy
                : HealthStatus.Failed,
            Reason = (_listener == null || !_listener.IsListening)
                ? "Relay server not listening"
                : "",
            LastChecked = DateTime.Now
        };
    }

    public void Shutdown()
    {
        _cts.Cancel();
        if (_listener != null && _listener.IsListening)
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        Shutdown();
        _cts.Dispose();
    }
}
