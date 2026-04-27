using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;

// Anonymous failure reporting. Fires on end-of-cascade failure (every strategy returned null and
// we're falling back to tier4 passthrough), if the user has opted in.
//
// Privacy contract — these rules apply on the client. The server re-validates everything on
// receipt:
//
// 1. The full URL is NEVER transmitted. Only the bare domain (host minus "www."). Paths and query
//    strings are reduced to a 12-char hex hash (SHA256 prefix) so the same failing video correlates
//    to the same hash in Discord without revealing which video.
// 2. For YouTube specifically, the video ID is also hashed.
// 3. Free-form error strings (yt-dlp stderr, exception messages) are run through a sanitizer that
//    strips Windows usernames, Linux home paths, IPv4 literals, the original URL, the machine name,
//    and long-token-shaped sequences. Anything that survives sanitization can still be rejected by
//    the server's secondary scan.
// 4. NO local file paths, NO username, NO machine name, NO IPs, NO cookies, NO tokens.
// 5. Reports are rate-limited client-side: at most one per cascade-failure, at most 1 every 30
//    seconds, and a 20-per-session cap. The server does its own per-IP rate limit on top.
public class ReportingService
{
    private readonly Logger _logger;
    private readonly SettingsManager _settings;
    private readonly SystemEventBus? _eventBus;
    private readonly HttpClient _http;
    private bool _promptedThisSession;
    private readonly object _promptLock = new();

    // Per-session counters and rate limits. Wiped on app restart.
    private DateTime _lastReportSent = DateTime.MinValue;
    private int _reportsThisSession = 0;
    private const int MaxReportsPerSession = 20;
    private static readonly TimeSpan MinIntervalBetweenReports = TimeSpan.FromSeconds(30);

    private readonly string _appVersion;
    private readonly string _machineName;

    // Endpoint. Cloudflare's geo-router puts this in front of the nearest node automatically; the
    // node's nginx /api proxy forwards to the FailureReportService route.
    private const string ReportEndpoint = "https://whyknot.dev/api/report";

    public ReportingService(Logger logger, SettingsManager settings, string appVersion, SystemEventBus? eventBus = null)
    {
        _logger = logger;
        _settings = settings;
        _eventBus = eventBus;
        _appVersion = appVersion ?? "unknown";
        _machineName = Environment.MachineName ?? "";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WKVRCProxy/" + _appVersion);
    }

    public bool IsEnabled => _settings.Config.EnableAnonymousReporting;
    public bool HasUserAnswered => _settings.Config.AnonymousReportingPromptAnswered;

    public void RecordUserAnswer(bool optIn)
    {
        _settings.Config.EnableAnonymousReporting = optIn;
        _settings.Config.AnonymousReportingPromptAnswered = true;
        _settings.Save();
        _logger.Info("[Reporting] User " + (optIn ? "opted in to" : "declined") + " anonymous failure reporting.");
    }

    // Build and (if opted in) send a failure report. Returns the JSON body that would be / was sent
    // — the UI can render this in the opt-in dialog as a "this is what gets sent" preview.
    public async Task<string?> ReportCascadeFailureAsync(CascadeFailureContext failure, CancellationToken ct = default)
    {
        if (failure == null) return null;

        try
        {
            var payload = BuildPayload(failure);
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });

            // First cascade failure of the session and the user hasn't answered the opt-in
            // question yet: surface a prompt to the UI carrying the sanitized preview so the
            // user can decide based on the actual data. We only publish once per session to
            // avoid stacking modals if multiple plays fail back-to-back.
            if (!_settings.Config.AnonymousReportingPromptAnswered)
            {
                bool shouldPrompt;
                lock (_promptLock)
                {
                    shouldPrompt = !_promptedThisSession;
                    if (shouldPrompt) _promptedThisSession = true;
                }
                if (shouldPrompt)
                {
                    string preview = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                    _eventBus?.PublishPrompt("ReportingService", "anonymousReportingOptIn", new { preview });
                    _logger.Info("[Reporting] Cascade failure observed; user has not answered opt-in — surfacing prompt.");
                }
                return json;
            }

            if (!IsEnabled) return json; // user declined; build the payload for logging only
            if (!RateLimitAllow()) { _logger.Debug("[Reporting] Rate-limited; skipping send."); return json; }

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(ReportEndpoint, content, ct);
            if (resp.IsSuccessStatusCode)
            {
                _reportsThisSession++;
                _lastReportSent = DateTime.UtcNow;
                _logger.Debug("[Reporting] Sent (HTTP " + (int)resp.StatusCode + ").");
            }
            else
            {
                string body = await SafeReadBody(resp);
                _logger.Warning("[Reporting] Server rejected report (HTTP " + (int)resp.StatusCode + "): " + body);
            }
            return json;
        }
        catch (TaskCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.Warning("[Reporting] Send failed: " + ex.Message);
            return null;
        }
    }

    // Render a sanitized preview without sending. Used by the first-launch opt-in modal so the
    // user can see exactly what the report contains before deciding.
    public string BuildPreview(CascadeFailureContext failure)
    {
        var payload = BuildPayload(failure);
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private bool RateLimitAllow()
    {
        if (_reportsThisSession >= MaxReportsPerSession) return false;
        if (DateTime.UtcNow - _lastReportSent < MinIntervalBetweenReports) return false;
        return true;
    }

    private static async Task<string> SafeReadBody(HttpResponseMessage resp)
    {
        try { return (await resp.Content.ReadAsStringAsync()) ?? ""; }
        catch { return "(no body)"; }
    }

    // Public for unit testing.
    public Dictionary<string, object?> BuildPayload(CascadeFailureContext failure)
    {
        string domain = ExtractDomain(failure.OriginalUrl);
        string path = ExtractPath(failure.OriginalUrl);
        string? videoId = ExtractYouTubeVideoId(failure.OriginalUrl);

        bool isLive = !string.IsNullOrEmpty(failure.OriginalUrl)
            && (failure.OriginalUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                || failure.OriginalUrl.Contains("/live/", StringComparison.OrdinalIgnoreCase));

        var strategies = new List<Dictionary<string, object?>>();
        foreach (var s in failure.StrategiesAttempted ?? Array.Empty<StrategyOutcome>())
        {
            strategies.Add(new Dictionary<string, object?>
            {
                ["name"] = s.Name,
                ["outcome"] = s.Outcome,
                ["errorClass"] = s.ErrorClass,
                ["latencyMs"] = s.LatencyMs,
            });
        }

        return new Dictionary<string, object?>
        {
            ["appVersion"] = _appVersion,
            ["failureKind"] = "AllStrategiesFailed",
            ["urlDomain"] = domain,
            ["player"] = string.IsNullOrEmpty(failure.Player) ? "avpro" : failure.Player.ToLowerInvariant(),
            ["streamType"] = isLive ? "live" : "vod",
            ["urlPathHashShort"] = string.IsNullOrEmpty(path) ? null : Sha256Short(path),
            ["videoIdHashShort"] = string.IsNullOrEmpty(videoId) ? null : Sha256Short(videoId),
            ["errorSummary"] = string.IsNullOrEmpty(failure.ErrorSummary) ? null : Sanitize(failure.ErrorSummary!),
            ["strategiesAttempted"] = strategies,
        };
    }

    // Public for unit testing — sanitization is the entire privacy story; tests assert behavior.
    public string Sanitize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string s = raw;

        // Windows: C:\Users\<name>\... → C:\Users\<USER>\...
        s = Regex.Replace(s, @"([A-Za-z]:\\Users\\)[^\\""'<>|*?\s]+", "$1<USER>", RegexOptions.IgnoreCase);
        // Linux: /home/<name>/... → /home/<USER>/...
        s = Regex.Replace(s, @"(/home/)[^/\s""'<>]+", "$1<USER>", RegexOptions.IgnoreCase);
        // Drop any remaining drive-letter paths so leaked install dirs don't slip through.
        s = Regex.Replace(s, @"[A-Za-z]:\\[^\s""'<>|]+", "<PATH>", RegexOptions.IgnoreCase);
        // IPv4 literals.
        s = Regex.Replace(s, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", "<IP>");
        // Long hex/base64 sequences (cookies, tokens). 24+ chars of [A-Za-z0-9_-+/=].
        s = Regex.Replace(s, @"[A-Za-z0-9_\-+/=]{24,}", m => m.Length > 40 ? "<TOKEN>" : m.Value);
        // Machine hostname (best effort — covers DNS-looking machine names).
        if (!string.IsNullOrEmpty(_machineName) && _machineName.Length >= 3)
            s = Regex.Replace(s, Regex.Escape(_machineName), "<HOST>", RegexOptions.IgnoreCase);
        // Cap.
        if (s.Length > 500) s = s.Substring(0, 499) + "…";
        return s;
    }

    // Public for unit testing.
    public static string ExtractDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "unknown";
        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            string host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            return host;
        }
        catch { return "unknown"; }
    }

    public static string ExtractPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            return (uri.AbsolutePath ?? "") + (uri.Query ?? "");
        }
        catch { return ""; }
    }

    public static string? ExtractYouTubeVideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            string host = uri.Host.ToLowerInvariant();
            if (host == "youtu.be") return uri.AbsolutePath.TrimStart('/');
            if (!host.EndsWith("youtube.com")) return null;
            var match = Regex.Match(uri.Query, @"[?&]v=([A-Za-z0-9_\-]{6,32})");
            if (match.Success) return match.Groups[1].Value;
            // Shorts URLs: /shorts/<id>
            var sm = Regex.Match(uri.AbsolutePath, @"/shorts/([A-Za-z0-9_\-]{6,32})");
            if (sm.Success) return sm.Groups[1].Value;
            return null;
        }
        catch { return null; }
    }

    public static string Sha256Short(string input)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder();
        for (int i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString(); // 12 hex chars
    }
}

// Caller-supplied snapshot of a cascade failure. Built by ResolutionEngine at the tier-4 fallback
// site. Free-form text fields go through ReportingService.Sanitize before transmission.
public class CascadeFailureContext
{
    public string OriginalUrl { get; set; } = "";
    public string Player { get; set; } = "avpro";
    public string? ErrorSummary { get; set; }
    public IReadOnlyList<StrategyOutcome>? StrategiesAttempted { get; set; }
}

public class StrategyOutcome
{
    public string Name { get; set; } = "";
    public string Outcome { get; set; } = "failed"; // "failed" | "timeout" | "skipped"
    public string? ErrorClass { get; set; }
    public int? LatencyMs { get; set; }
}
