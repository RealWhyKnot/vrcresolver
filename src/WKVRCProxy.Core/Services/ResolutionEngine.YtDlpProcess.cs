using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.IPC;

namespace WKVRCProxy.Core.Services;

// Partial class — yt-dlp / yt-dlp-og / streamlink subprocess invocation, stdout parsing, the
// probe-header dictionaries that shape pre-flight requests to look like AVPro's first fetch,
// the bgutil PO-token plugin argv assembly, and the YouTube bot-detection regex. Anything
// process-shaped or yt-dlp-shaped lives here so the cascade orchestration in ResolutionEngine.cs
// stays focused on tier sequencing rather than subprocess plumbing.
[SupportedOSPlatform("windows")]
public partial class ResolutionEngine
{
    // Probe headers are shaped to look like the first request AVPro/UnityPlayer sends when it
    // opens a stream — NOT like a scanner. Anti-bot CDNs (YouTube, Cloudflare, Akamai) fingerprint
    // probe-like requests (no UA, HEAD, Range: bytes=0-0, empty Accept) and return 403 for ones
    // that look synthetic. We send: AVPro's real UA, a realistic Accept set, gzip/identity encoding
    // (AVPro doesn't compress range reads), and a DASH/MP4-typical initial segment range. Combined
    // with curl-impersonate's Chrome TLS fingerprint, the request is indistinguishable from an
    // actual playback start. Keep these changes in lockstep with any UA/header tweaks made
    // elsewhere — inconsistency is itself a fingerprint.
    private static Dictionary<string, string> BuildBinaryProbeHeaders() => new()
    {
        ["User-Agent"] = VrchatAvProUserAgent,
        ["Accept"] = "*/*",
        ["Accept-Language"] = "en-US,en;q=0.9",
        ["Accept-Encoding"] = "identity;q=1, *;q=0",
        ["Range"] = "bytes=0-4095",
        ["Connection"] = "keep-alive",
    };

    private static Dictionary<string, string> BuildHlsProbeHeaders() => new()
    {
        // HLS manifests: AVPro fetches them as plain GETs, typically with a browser-shaped UA
        // (some AVPro builds use the OS WebView UA for manifest fetches, others use UnityPlayer).
        // UnityPlayer UA is the more conservative choice — matches what the native-UA deny-list
        // hosts expect anyway.
        ["User-Agent"] = VrchatAvProUserAgent,
        ["Accept"] = "application/vnd.apple.mpegurl, application/x-mpegurl, */*;q=0.8",
        ["Accept-Language"] = "en-US,en;q=0.9",
        ["Accept-Encoding"] = "gzip, deflate, identity",
        ["Connection"] = "keep-alive",
    };

    // Scans yt-dlp stderr for well-known YouTube bot-detection phrases. YouTube emits a curly
    // right single quote (U+2019) in "you're"; normalize to the straight ASCII apostrophe so a
    // single set of literals covers both the canonical and the wire form. Without this, the
    // detector silently misses every real bot-detection error and MarkDomainRequiresPot never
    // fires, so the PO-upgrade flywheel stays cold for youtube.com.
    public static bool IsBotDetectionStderr(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        string normalized = stderr.Replace('’', '\'');
        return normalized.Contains("Sign in to confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Sign in to confirm you are not a bot", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase);
    }

    // Build the yt-dlp CLI fragment that wires in the bgutil yt-dlp plugin. yt-dlp loads the plugin
    // from pluginDir and uses it to resolve the youtubepot-bgutilhttp: extractor-arg by calling the
    // sidecar at http://localhost:{potPort} at request time. The plugin mints PO tokens bound to
    // yt-dlp's own visitor_data, which is the only binding YouTube actually accepts.
    //
    // Returns an empty list when inputs are not ready (no port, no plugin dir) — caller is expected
    // to have already confirmed the plugin dir exists on disk; this helper is pure to keep unit
    // tests focused on the arg shape (and to guard against a regression back to the old broken
    // "youtube:po_token=web.gvs+TOKEN" manual-injection path).
    public static List<string> BuildBgutilPluginArgs(string pluginDir, int potPort)
    {
        if (potPort <= 0 || string.IsNullOrWhiteSpace(pluginDir)) return new List<string>();
        return new List<string>
        {
            "--plugin-dirs", pluginDir,
            // player_js_variant=main avoids an 'origin' TypeError in the TV player variant that
            // yt-dlp otherwise picks.
            "--extractor-args", "youtube:player_js_variant=main",
            // bgutil lives under its own extractor scope (youtubepot-bgutilhttp) — it MUST be a
            // separate --extractor-args flag. Packing it into the youtube: string after a semicolon
            // makes yt-dlp interpret the whole "youtubepot-bgutilhttp:base_url=..." as a youtube
            // key, so the plugin never sees our base_url and silently falls back to the hardcoded
            // 127.0.0.1:4416 default — which is not what we're listening on.
            "--extractor-args", "youtubepot-bgutilhttp:base_url=http://localhost:" + potPort
        };
    }

    // yt-dlp-og.exe lives in the VRChat Tools folder (created by PatcherService as a backup).
    // streamlink.exe lives in tools/streamlink/bin/ (portable zip layout) or tools/streamlink/.
    // All other binaries (yt-dlp.exe, redirector.exe) live in dist/tools/.
    private string GetBinaryPath(string binary)
    {
        if (binary == "streamlink.exe")
        {
            string slBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "streamlink");
            string slBin = Path.Combine(slBase, "bin", binary);
            if (File.Exists(slBin)) return slBin;
            return Path.Combine(slBase, binary);
        }
        if (binary == "yt-dlp-og.exe")
        {
            string? toolsDir = _patcher.VrcToolsDir;
            if (!string.IsNullOrEmpty(toolsDir))
            {
                string vrcPath = Path.Combine(toolsDir, binary);
                if (File.Exists(vrcPath)) return vrcPath;
            }
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", binary);
    }

    private async Task<(YtDlpResult? Result, string Stderr)> RunYtDlp(string binary, List<string> args, RequestContext ctx, int timeoutMs = 15000, CancellationToken ct = default)
    {
        string path = GetBinaryPath(binary);
        if (!File.Exists(path))
        {
            _logger.Error("[" + ctx.CorrelationId + "] " + binary + " not found at: " + path);
            return (null, "");
        }

        // Sanitize args for logging — mask PO token value (it's long and security-sensitive)
        string loggableArgs = string.Join(" ", args.Select(a => a.StartsWith("youtube:po_token=") ? "youtube:po_token=[REDACTED]" : a));
        _logger.Debug("[" + ctx.CorrelationId + "] Executing: " + binary + " " + loggableArgs);

        try
        {
            var stdoutLines = new List<string>();
            var stdoutLock = new object();
            var urlSeenTcs = new TaskCompletionSource<bool>();

            using var process = new Process();
            process.StartInfo.FileName = path;
            process.StartInfo.Arguments = string.Join(" ", args.Select(a => "\"" + a + "\""));
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.OutputDataReceived += (s, e) => {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                string line = e.Data.Trim();
                lock (stdoutLock) stdoutLines.Add(line);
                // Signal early when a URL line appears, but don't complete — we still need the meta line.
                if (line.StartsWith("url:") || line.StartsWith("http"))
                    urlSeenTcs.TrySetResult(true);
            };

            // Capture stderr so errors from yt-dlp are visible in the log instead of silently discarded.
            var stderrLines = new StringBuilder();
            process.ErrorDataReceived += (s, e) => {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    stderrLines.AppendLine(e.Data.Trim());
            };

            process.Start();
            ProcessGuard.Register(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // External cancel kills the child immediately (race winner found, shutdown). Without this,
            // a losing race participant runs to its full timeoutMs even after the winner is committed,
            // burning CPU and the per-host budget for the next request.
            using var killReg = ct.Register(() => {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* race with natural exit */ }
            });

            var exitTcs = new TaskCompletionSource<bool>();
            _ = Task.Run(() => {
                try { process.WaitForExit(); }
                catch (ObjectDisposedException) { /* process disposed on timeout path — expected */ }
                catch (InvalidOperationException) { /* process never started or already cleaned up */ }
                exitTcs.TrySetResult(true);
            });

            var timeoutTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(exitTcs.Task, timeoutTask);

            // Log any stderr output regardless of whether it timed out or resolved
            string stderrOutput = stderrLines.ToString().Trim();
            if (!string.IsNullOrEmpty(stderrOutput))
                _logger.Warning("[" + ctx.CorrelationId + "] [" + binary + "] stderr: " + stderrOutput);

            if (completed == timeoutTask)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] " + binary + " timed out after " + (timeoutMs / 1000) + "s.");
                _eventBus?.PublishError("ResolutionEngine", new ErrorContext {
                    Category = ErrorCategory.ChildProcess,
                    Code = ErrorCodes.YTDLP_TIMEOUT,
                    Summary = binary + " timed out after " + (timeoutMs / 1000) + " seconds",
                    Detail = "The process did not produce a URL within the timeout window",
                    ActionHint = "The video source may be slow to respond. Try again or switch to a different tier.",
                    IsRecoverable = true
                }, ctx.CorrelationId);
                try { process.Kill(); } catch { /* Process may have already exited */ }
                return (null, stderrOutput);
            }

            // Race-cancellation path: killReg already terminated the process and exitTcs completed,
            // so we don't have a useful URL/exit code to report. Return silently â€” no warning, no
            // ERROR event â€” because the winning strategy is what the user actually wants.
            if (ct.IsCancellationRequested)
            {
                _logger.Debug("[" + ctx.CorrelationId + "] " + binary + " cancelled by caller (race winner found).");
                return (null, stderrOutput);
            }

            // Non-zero exit codes are almost always the reason yt-dlp returned no URL
            if (process.HasExited && process.ExitCode != 0)
                _logger.Warning("[" + ctx.CorrelationId + "] " + binary + " exited with non-zero code " + process.ExitCode + ".");

            List<string> linesSnapshot;
            lock (stdoutLock) linesSnapshot = new List<string>(stdoutLines);

            var parsed = ParseYtDlpOutput(linesSnapshot);
            if (parsed == null)
            {
                _logger.Warning("[" + ctx.CorrelationId + "] [" + binary + "] Process exited without outputting a URL (check stderr above).");
                return (null, stderrOutput);
            }

            string shortUrl = parsed.Url.Length > 100 ? parsed.Url.Substring(0, 100) + "..." : parsed.Url;
            string metaSummary = parsed.Height.HasValue ? parsed.Height + "p " + (parsed.Vcodec ?? "?") : "(no metadata)";
            _logger.Debug("[" + ctx.CorrelationId + "] [" + binary + "] resolved: " + shortUrl + " [" + metaSummary + "]");
            return (parsed, stderrOutput);
        }
        catch (Exception ex)
        {
            _logger.Error("[" + ctx.CorrelationId + "] " + binary + " execution error: " + ex.Message, ex);
            _eventBus?.PublishError("ResolutionEngine", new ErrorContext {
                Category = ErrorCategory.ChildProcess,
                Code = ErrorCodes.YTDLP_EXECUTION_ERROR,
                Summary = binary + " failed to execute",
                Detail = ex.Message,
                ActionHint = "The binary may be corrupted or missing. Try reinstalling WKVRCProxy.",
                IsRecoverable = false
            }, ctx.CorrelationId);
            return (null, "");
        }
    }

    // Parses yt-dlp stdout. Expects either:
    //   url:<url>                                            (tier 1 with --print url:%(url)s)
    //   meta:<height>|<width>|<vcodec>|<format_id>|<protocol>  (tier 1 with --print meta:...)
    // or a plain first-line URL (yt-dlp-og, streamlink). `NA` or empty fields → null.
    public static YtDlpResult? ParseYtDlpOutput(List<string> lines)
    {
        string? url = null;
        int? height = null, width = null;
        string? vcodec = null, formatId = null, protocol = null;

        foreach (var line in lines)
        {
            if (url == null && line.StartsWith("url:"))
            {
                string rest = line.Substring(4).Trim();
                if (rest.StartsWith("http")) url = rest;
            }
            else if (url == null && line.StartsWith("http"))
            {
                url = line;
            }
            else if (line.StartsWith("meta:"))
            {
                var parts = line.Substring(5).Split('|');
                if (parts.Length >= 1) height = ParseNullableInt(parts[0]);
                if (parts.Length >= 2) width = ParseNullableInt(parts[1]);
                if (parts.Length >= 3) vcodec = NullIfEmpty(parts[2]);
                if (parts.Length >= 4) formatId = NullIfEmpty(parts[3]);
                if (parts.Length >= 5) protocol = NullIfEmpty(parts[4]);
            }
        }

        return url == null ? null : new YtDlpResult(url, height, width, vcodec, formatId, protocol);
    }

    private static int? ParseNullableInt(string s)
    {
        s = s.Trim();
        if (string.IsNullOrEmpty(s) || s == "NA" || s == "None") return null;
        return int.TryParse(s, out var v) ? v : null;
    }

    private static string? NullIfEmpty(string s)
    {
        s = s.Trim();
        return (string.IsNullOrEmpty(s) || s == "NA" || s == "None") ? null : s;
    }
}
