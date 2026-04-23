using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

[SupportedOSPlatform("windows")]
public class PotProviderService : IProxyModule, IDisposable
{
    public string Name => "PotProviderService";
    
    private Logger? _logger;
    private Process? _providerProcess;
    private int _port = 0;

    // Exposed so ResolutionEngine can build the youtubepot-bgutilhttp:base_url=http://localhost:{port}
    // extractor-arg that yt-dlp's bgutil plugin reads. Zero until InitializeAsync picks a port.
    public int Port => _port;
    // bgutil sidecar token generation takes 8-15s on fresh cache (it spins up an internal session).
    // Prior 10s timeout was killing valid requests that would have completed within 1-2s.
    private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    
    // Cache: VideoId -> (Token, Expiry)
    private readonly ConcurrentDictionary<string, (string token, DateTime expires)> _tokenCache = new();

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;

        try
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            _port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "bgutil-ytdlp-pot-provider.exe");

            _logger.Debug("[PotProvider] Allocated port " + _port + " for bgutil sidecar.");

            if (File.Exists(exePath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-p " + _port,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _providerProcess = Process.Start(psi);
                ProcessGuard.Register(_providerProcess);
                _logger.Info("[PotProvider] Started bgutil-ytdlp-pot-provider (PID " + (_providerProcess?.Id.ToString() ?? "?") + ") on port " + _port + ".");

                // Pipe stdout/stderr to logger so child process output is visible
                if (_providerProcess != null)
                {
                    _ = Task.Run(async () => {
                        try
                        {
                            using var reader = _providerProcess.StandardOutput;
                            string? line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                if (!string.IsNullOrEmpty(line))
                                    _logger?.Debug("[PotProvider] " + line);
                            }
                        }
                        catch { /* Process exited or stream closed */ }
                    });
                    _ = Task.Run(async () => {
                        try
                        {
                            using var reader = _providerProcess.StandardError;
                            string? line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                if (!string.IsNullOrEmpty(line))
                                    _logger?.Warning("[PotProvider] " + line);
                            }
                        }
                        catch { /* Process exited or stream closed */ }
                    });
                }
            }
            else
            {
                _logger.Warning("bgutil-ytdlp-pot-provider.exe not found. PO Token spoofing may fail.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to initialize PO Token provider: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    public async Task<string?> GetPotTokenAsync(string visitorData, string videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return null;

        string shortId = videoId.Substring(0, Math.Min(8, videoId.Length)) + (videoId.Length > 8 ? "..." : "");

        // Cache hit
        if (_tokenCache.TryGetValue(videoId, out var cached))
        {
            if (cached.expires > DateTime.Now)
            {
                _logger?.Debug("[PotProvider] Cache hit for video " + shortId + " (expires " + cached.expires.ToString("HH:mm:ss") + ").");
                return cached.token;
            }
            _tokenCache.TryRemove(videoId, out _);
            _logger?.Debug("[PotProvider] Cache expired for video " + shortId + " — fetching fresh token.");
        }
        else
        {
            _logger?.Debug("[PotProvider] Cache miss for video " + shortId + " — fetching token.");
        }

        if (_port == 0)
        {
            _logger?.Error("[PotProvider] Port is 0 — bgutil sidecar was never started. PO tokens unavailable; yt-dlp will likely fail YouTube bot detection.");
            return null;
        }

        // Guard: if the bgutil process has crashed, the HTTP call will just time out (10s).
        // Attempt to restart it automatically rather than leaving PO tokens dead for the session.
        if (_providerProcess != null && _providerProcess.HasExited)
        {
            _logger?.Warning("[PotProvider] bgutil-ytdlp-pot-provider crashed (exit code " + _providerProcess.ExitCode + ") — attempting restart.");
            await RestartProviderAsync();
            if (_providerProcess == null || _providerProcess.HasExited)
            {
                _logger?.Error("[PotProvider] bgutil restart failed — PO tokens unavailable for this request.");
                return null;
            }
            // Give the restarted server a moment to start listening before the HTTP POST
            await Task.Delay(800);
        }

        try
        {
            // Use "localhost" rather than "127.0.0.1" so .NET's SocketsHttpHandler can
            // try both IPv4 (127.0.0.1) and IPv6 (::1). bgutil's Deno HTTP server binds to
            // [::]:PORT (IPv6 wildcard) which refuses pure-IPv4 connections on some Windows
            // configurations; localhost lets the stack negotiate the right address family.
            string url = "http://localhost:" + _port + "/get_pot";
            var payload = new
            {
                client = "web.gvs",
                visitorData = visitorData,
                dataSyncId = videoId
            };

            string json = JsonSerializer.Serialize(payload);

            // If bgutil just started, it may not be listening yet. Retry once after a brief delay
            // rather than failing the entire resolution on the very first video request.
            // Each attempt gets a fresh StringContent — HttpClient may partially read the content
            // stream even before a connection-refused error, so reusing it could send an empty body.
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            }
            catch (HttpRequestException ex)
            {
                _logger?.Debug("[PotProvider] First attempt failed (" + ex.Message + ") — bgutil may still be starting. Retrying in 600ms...");
                await Task.Delay(600);
                response = await _httpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            }

            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);

            if (doc.RootElement.TryGetProperty("poToken", out var tokenObj))
            {
                string token = tokenObj.GetString() ?? "";
                if (string.IsNullOrEmpty(token))
                {
                    _logger?.Warning("[PotProvider] bgutil returned an empty poToken for video " + shortId + ".");
                    return null;
                }
                _tokenCache[videoId] = (token, DateTime.Now.AddHours(4));
                _logger?.Debug("[PotProvider] Token acquired and cached for video " + shortId + " (expires in 4h).");
                return token;
            }

            _logger?.Warning("[PotProvider] Response did not contain 'poToken' field for video " + shortId + ". Body: " + responseJson.Substring(0, Math.Min(200, responseJson.Length)));
        }
        catch (Exception ex)
        {
            _logger?.Error("[PotProvider] Token fetch failed for video " + shortId + " (" + ex.GetType().Name + "): " + ex.Message);
        }

        return null;
    }

    private Task RestartProviderAsync()
    {
        try
        {
            // Clean up the old (crashed) process handle
            try { _providerProcess?.Dispose(); } catch { }
            _providerProcess = null;

            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "bgutil-ytdlp-pot-provider.exe");
            if (!File.Exists(exePath))
            {
                _logger?.Error("[PotProvider] Cannot restart — bgutil-ytdlp-pot-provider.exe not found.");
                return Task.CompletedTask;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-p " + _port,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _providerProcess = Process.Start(psi);
            if (_providerProcess == null)
            {
                _logger?.Error("[PotProvider] bgutil restart: Process.Start returned null.");
                return Task.CompletedTask;
            }

            ProcessGuard.Register(_providerProcess);
            _logger?.Info("[PotProvider] bgutil restarted (PID " + _providerProcess.Id + ") on port " + _port + ".");

            // Pipe stdout/stderr from restarted process
            _ = Task.Run(async () => {
                try { using var r = _providerProcess.StandardOutput; string? l; while ((l = await r.ReadLineAsync()) != null) if (!string.IsNullOrEmpty(l)) _logger?.Debug("[PotProvider] " + l); } catch { }
            });
            _ = Task.Run(async () => {
                try { using var r = _providerProcess.StandardError; string? l; while ((l = await r.ReadLineAsync()) != null) if (!string.IsNullOrEmpty(l)) _logger?.Warning("[PotProvider] " + l); } catch { }
            });
        }
        catch (Exception ex)
        {
            _logger?.Error("[PotProvider] bgutil restart failed: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    public ModuleHealthReport GetHealthReport()
    {
        if (_providerProcess != null && !_providerProcess.HasExited && _port > 0)
        {
            return new ModuleHealthReport
            {
                ModuleName = Name,
                Status = HealthStatus.Healthy,
                Reason = "",
                LastChecked = DateTime.Now
            };
        }

        bool binaryExists = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "bgutil-ytdlp-pot-provider.exe"));
        if (!binaryExists)
        {
            return new ModuleHealthReport
            {
                ModuleName = Name,
                Status = HealthStatus.Degraded,
                Reason = "bgutil-ytdlp-pot-provider.exe not found",
                LastChecked = DateTime.Now
            };
        }

        string reason = "PO Token provider process not running";
        if (_providerProcess != null && _providerProcess.HasExited)
            reason = "PO Token provider process crashed (exit code " + _providerProcess.ExitCode + ")";

        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = HealthStatus.Failed,
            Reason = reason,
            LastChecked = DateTime.Now
        };
    }

    public void Shutdown()
    {
        if (_providerProcess != null && !_providerProcess.HasExited)
        {
            try { _providerProcess.Kill(true); }
            catch { /* Shutdown cleanup — failure is expected */ }
            try { _providerProcess.Dispose(); }
            catch { /* Shutdown cleanup — failure is expected */ }
        }
    }

    public void Dispose()
    {
        Shutdown();
        _httpClient.Dispose();
    }
}
