using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

// Checks GitHub releases for a newer WKVRCProxy build. Mirrors YtDlpUpdater's contract (status
// enum + event) so the IPC surface is uniform. Does not download or apply anything itself —
// that is updater.exe's job. This module only signals the UI so the user can choose to update.
//
// Version stamp format is "YYYY.M.d.<count>-<UID>". Tags are expected to be "v<FullVersion>".
// Comparison strips the leading "v" and the trailing "-UID" then parses with System.Version,
// since lexicographic compare across single/double-digit days breaks ordering.
[SupportedOSPlatform("windows")]
public class AppUpdateChecker : IProxyModule
{
    public string Name => "AppUpdateChecker";

    private Logger? _logger;
    private SystemEventBus? _eventBus;
    private readonly HttpClient _http;

    private string _localVersion = "";
    private string _remoteVersion = "";
    private string _releaseUrl = "";
    private string _downloadUrl = "";
    private UpdateStatus _lastStatus = UpdateStatus.Idle;
    private string _lastStatusDetail = "";

    public enum UpdateStatus { Idle, Checking, UpToDate, UpdateAvailable, Failed }

    public string LocalVersion => _localVersion;
    public string RemoteVersion => _remoteVersion;
    public string ReleaseUrl => _releaseUrl;
    public string DownloadUrl => _downloadUrl;
    public UpdateStatus Status => _lastStatus;
    public string StatusDetail => _lastStatusDetail;

    public event Action<UpdateStatus, string, string, string, string, string>? OnStatusChanged;

    private const string LatestReleaseUrl = "https://api.github.com/repos/RealWhyKnot/WKVRCProxy/releases/latest";

    public AppUpdateChecker()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WKVRCProxy-AppUpdateChecker/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _eventBus = context.EventBus;
        _localVersion = ReadLocalVersion();
        _ = Task.Run(CheckAsync);
        return Task.CompletedTask;
    }

    private static string ReadLocalVersion()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
            if (File.Exists(path)) return File.ReadAllText(path).Trim();
        }
        catch { }
        return "";
    }

    public async Task CheckAsync()
    {
        try
        {
            SetStatus(UpdateStatus.Checking, "Checking for WKVRCProxy updates...");

            if (string.IsNullOrEmpty(_localVersion))
            {
                SetStatus(UpdateStatus.Failed, "version.txt missing — cannot determine local version.");
                return;
            }

            using var resp = await _http.GetAsync(LatestReleaseUrl);
            if (!resp.IsSuccessStatusCode)
            {
                SetStatus(UpdateStatus.Failed, "GitHub API returned " + (int)resp.StatusCode + ".");
                return;
            }

            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(tag))
            {
                SetStatus(UpdateStatus.Failed, "Latest release has no tag.");
                return;
            }

            _remoteVersion = tag;
            _releaseUrl = htmlUrl;

            // Find the bundle zip asset (WKVRCProxy-<version>.zip).
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    if (name.StartsWith("WKVRCProxy-", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        _downloadUrl = asset.TryGetProperty("browser_download_url", out var dlEl) ? dlEl.GetString() ?? "" : "";
                        break;
                    }
                }
            }

            if (CompareVersions(_localVersion, tag) >= 0)
            {
                SetStatus(UpdateStatus.UpToDate, "Local " + _localVersion + " is current.");
                return;
            }

            SetStatus(UpdateStatus.UpdateAvailable, "Update " + _localVersion + " → " + tag + " available.");
        }
        catch (Exception ex)
        {
            SetStatus(UpdateStatus.Failed, ex.Message);
        }
    }

    // Returns: <0 if local older, 0 if equal, >0 if local newer. Strips "v" prefix and "-UID"
    // suffix, then compares the YYYY.M.d.count chunk as System.Version. Falls back to ordinal
    // string compare if anything fails to parse, so a malformed tag never silently looks newer.
    public static int CompareVersions(string local, string remote)
    {
        try
        {
            string a = Strip(local);
            string b = Strip(remote);
            if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
                return va.CompareTo(vb);
        }
        catch { }
        return string.Compare(local, remote, StringComparison.OrdinalIgnoreCase);

        static string Strip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "0.0.0.0";
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            int dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);
            return s;
        }
    }

    private void SetStatus(UpdateStatus status, string detail)
    {
        _lastStatus = status;
        _lastStatusDetail = detail;
        switch (status)
        {
            case UpdateStatus.Failed: _logger?.Warning("[AppUpdateChecker] " + detail); break;
            case UpdateStatus.UpdateAvailable: _logger?.Info("[AppUpdateChecker] " + detail); break;
            default: _logger?.Debug("[AppUpdateChecker] " + detail); break;
        }
        try { OnStatusChanged?.Invoke(status, detail, _localVersion, _remoteVersion, _releaseUrl, _downloadUrl); }
        catch { /* UI dispatch failures must not break the check pipeline */ }
    }

    public ModuleHealthReport GetHealthReport()
    {
        return _lastStatus switch
        {
            UpdateStatus.UpdateAvailable => new ModuleHealthReport
            {
                ModuleName = Name,
                Status = HealthStatus.Healthy,
                Reason = "Update " + _localVersion + " → " + _remoteVersion + " available.",
                LastChecked = DateTime.Now
            },
            UpdateStatus.Failed => new ModuleHealthReport
            {
                ModuleName = Name,
                Status = HealthStatus.Healthy, // Update-check failure does not degrade core function.
                Reason = "Update check failed: " + _lastStatusDetail,
                LastChecked = DateTime.Now
            },
            _ => new ModuleHealthReport
            {
                ModuleName = Name,
                Status = HealthStatus.Healthy,
                Reason = _lastStatusDetail,
                LastChecked = DateTime.Now
            }
        };
    }

    public void Shutdown()
    {
        try { _http.Dispose(); } catch { }
    }
}
