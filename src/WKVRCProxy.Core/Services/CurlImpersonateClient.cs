using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

[SupportedOSPlatform("windows")]
public class CurlImpersonateClient : IProxyModule
{
    public string Name => "CurlImpersonateClient";
    private Logger? _logger;
    private string _executablePath = "";
    public bool IsAvailable { get; private set; }

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _executablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "curl-impersonate-win.exe");
        
        if (File.Exists(_executablePath))
        {
            IsAvailable = true;
        }
        else
        {
            IsAvailable = false;
            _logger.Warning("curl-impersonate-win.exe not found at: " + _executablePath + ". Relay will use standard HttpClient for TLS-sensitive domains.");
        }

        return Task.CompletedTask;
    }

    public Task<Stream> SendRequestAsync(string method, string url, Dictionary<string, string> headers)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("--impersonate");
        psi.ArgumentList.Add("chrome116");
        psi.ArgumentList.Add("-s"); // silent
        psi.ArgumentList.Add("-i"); // include headers in output
        psi.ArgumentList.Add("-X");
        psi.ArgumentList.Add(method);

        foreach (var header in headers)
        {
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add(header.Key + ": " + header.Value);
        }

        psi.ArgumentList.Add(url);

        var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start curl-impersonate-win process.");
        ProcessGuard.Register(process);

        _logger?.Debug("Spawned curl-impersonate process for: " + method + " " + url.Substring(0, Math.Min(80, url.Length)));

        // Pipe stderr to logger asynchronously — wrapped in try/catch so I/O errors don't get silently dropped
        _ = Task.Run(async () => {
            try
            {
                using var reader = process.StandardError;
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!string.IsNullOrEmpty(line))
                        _logger?.Warning("[CURL-WARN] " + line);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning("curl-impersonate stderr reader error: " + ex.Message);
            }
        });

        // We return the raw output stream
        return Task.FromResult(process.StandardOutput.BaseStream);
    }

    // Checks whether a URL is reachable by sending a GET and parsing the HTTP status from
    // the response headers. The bundled curl-impersonate-win.exe is a minimal wrapper that
    // ONLY accepts: --impersonate, -s, -i, -X, -H, <url>. Anything else (-L, -D, -o,
    // --max-time) is silently dropped, which previously caused 100% of probes to "time out":
    // -D - was ignored so no HTTP/ status line ever hit stdout, the body went to stdout
    // instead of the discarded null device, and we read until EOF looking for a header that
    // never came. Use -i (supported) and parse the first HTTP/ line, then early-out at the
    // header/body separator so we don't drain the whole body. Timeout is enforced by the
    // C# CancellationToken since --max-time isn't available.
    public async Task<int> CheckReachabilityAsync(string url, Dictionary<string, string>? headers = null, int timeoutSeconds = 5)
    {
        if (!IsAvailable) return -1;

        var psi = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("--impersonate"); psi.ArgumentList.Add("chrome116");
        psi.ArgumentList.Add("-s");             // silent — no progress meter
        psi.ArgumentList.Add("-i");             // include response headers in stdout (then body)
        psi.ArgumentList.Add("-X"); psi.ArgumentList.Add("GET");

        if (headers != null)
        {
            foreach (var h in headers)
            {
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add(h.Key + ": " + h.Value);
            }
        }

        psi.ArgumentList.Add(url);

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process == null) return -1;
            ProcessGuard.Register(process);

            int? statusCode = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var reader = new StreamReader(process.StandardOutput.BaseStream);
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) != null)
            {
                if (statusCode == null && line.StartsWith("HTTP/"))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int s)) statusCode = s;
                }
                // Empty line marks end of headers — kill the process so we don't drain the
                // (potentially huge) response body just to discard it.
                else if (statusCode != null && line.Length == 0)
                {
                    break;
                }
            }

            if (process != null && !process.HasExited)
            {
                try { process.Kill(); } catch { /* Already gone */ }
            }
            else if (process != null && process.ExitCode != 0)
            {
                _logger?.Debug("curl-impersonate exited with code " + process.ExitCode + " for " + url.Substring(0, Math.Min(80, url.Length)));
            }

            return statusCode ?? -1;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception ex)
        {
            _logger?.Debug("curl-impersonate reachability check failed for " + url.Substring(0, Math.Min(80, url.Length)) + ": " + ex.Message);
            return -1;
        }
        finally
        {
            try { if (process != null && !process.HasExited) process.Kill(); } catch { /* Process may have already exited */ }
        }
    }

    public ModuleHealthReport GetHealthReport()
    {
        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = IsAvailable ? HealthStatus.Healthy : HealthStatus.Degraded,
            Reason = IsAvailable ? "" : "curl-impersonate-win.exe not found -- TLS fingerprint spoofing unavailable",
            LastChecked = DateTime.Now
        };
    }

    public void Shutdown() { }
}
