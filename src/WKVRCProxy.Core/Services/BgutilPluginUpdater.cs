using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

// Keeps tools/yt-dlp-plugins/yt_dlp_plugins in sync with bgutil-ytdlp-pot-provider's main branch.
// The plugin is what teaches yt-dlp to resolve youtubepot-bgutilhttp:base_url=... at request time
// and fetch PO tokens with correctly-bound visitor_data — the only path that actually works against
// YouTube bot detection on the native client (no cookies, no login).
//
// On startup (when AutoUpdateYtDlp is enabled):
// 1. Read the SHA we baked in at build-time from tools/yt-dlp-plugins/.version.
// 2. Query GitHub for the latest main commit SHA.
// 3. If they differ, download the zipball, extract only plugin/yt_dlp_plugins/, atomic-swap it into
//    place, and rewrite .version.
//
// Separate module from YtDlpUpdater so one can fail without tripping the other's health state.
// Reuses the same AutoUpdateYtDlp config gate — bgutil plugin and yt-dlp travel together.
[SupportedOSPlatform("windows")]
public class BgutilPluginUpdater : IProxyModule
{
    public string Name => "BgutilPluginUpdater";

    private Logger? _logger;
    private SettingsManager? _settings;
    private readonly HttpClient _http;

    private string _pluginRootDir = "";
    private string _versionFile = "";

    private string _localSha = "";
    private string _remoteSha = "";
    private string _lastFetchError = "";
    private UpdateStatus _lastStatus = UpdateStatus.Idle;
    private string _lastStatusDetail = "";

    public enum UpdateStatus { Idle, Checking, UpToDate, UpdateAvailable, Downloading, Updated, Failed, Disabled }

    public string LocalSha => _localSha;
    public string RemoteSha => _remoteSha;
    public UpdateStatus Status => _lastStatus;
    public string StatusDetail => _lastStatusDetail;

    public event Action<UpdateStatus, string, string, string>? OnStatusChanged;

    // bgutil-ytdlp-pot-provider has no tagged releases on main — it lives on main and ships via
    // commits. Track the commit SHA instead of a release tag. If `main` ever moves to `master`
    // again the mirror URL in build.ps1 already has both fallbacks; the updater only needs main.
    private const string LatestCommitUrl = "https://api.github.com/repos/Brainicism/bgutil-ytdlp-pot-provider/commits/main";
    private const string ZipballUrlFmt = "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/archive/{0}.zip";

    public BgutilPluginUpdater()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WKVRCProxy-BgutilPluginUpdater/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _settings = context.Settings;
        _pluginRootDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "yt-dlp-plugins");
        _versionFile = Path.Combine(_pluginRootDir, ".version");

        if (!_settings.Config.AutoUpdateYtDlp)
        {
            _lastStatus = UpdateStatus.Disabled;
            _lastStatusDetail = "Auto-update disabled in settings.";
            _logger.Debug("[BgutilPluginUpdater] AutoUpdateYtDlp is false — skipping plugin update check.");
            return Task.CompletedTask;
        }

        _ = Task.Run(CheckAndUpdateAsync);
        return Task.CompletedTask;
    }

    private async Task CheckAndUpdateAsync()
    {
        try
        {
            SetStatus(UpdateStatus.Checking, "Checking for bgutil plugin updates...");

            _localSha = ReadLocalSha();
            if (string.IsNullOrEmpty(_localSha))
            {
                _logger?.Debug("[BgutilPluginUpdater] No local .version marker — treating as uninstalled.");
            }
            else
            {
                _logger?.Debug("[BgutilPluginUpdater] Local bgutil plugin SHA: " + _localSha);
            }

            _remoteSha = await FetchLatestShaAsync();
            if (string.IsNullOrEmpty(_remoteSha))
            {
                string reason = string.IsNullOrEmpty(_lastFetchError) ? "unknown error" : _lastFetchError;
                SetStatus(UpdateStatus.Failed, "Failed to reach GitHub commits API: " + reason);
                return;
            }

            _logger?.Debug("[BgutilPluginUpdater] Latest main commit: " + _remoteSha);

            if (!string.IsNullOrEmpty(_localSha) && _localSha.StartsWith(_remoteSha, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(UpdateStatus.UpToDate, "Plugin at " + _localSha + " is current.");
                return;
            }

            SetStatus(UpdateStatus.UpdateAvailable,
                (string.IsNullOrEmpty(_localSha) ? "(none)" : _localSha) + " → " + _remoteSha + " available.");

            SetStatus(UpdateStatus.Downloading, "Downloading " + _remoteSha + "...");
            string assetUrl = string.Format(ZipballUrlFmt, _remoteSha);
            string? installed = await DownloadAndSwapAsync(assetUrl, _remoteSha);
            if (installed == null)
            {
                SetStatus(UpdateStatus.Failed, "Download or install failed — plugin left unchanged.");
                return;
            }

            _localSha = installed;
            SetStatus(UpdateStatus.Updated, "Updated bgutil plugin to " + _remoteSha + ".");
        }
        catch (Exception ex)
        {
            SetStatus(UpdateStatus.Failed, ex.Message);
        }
    }

    private void SetStatus(UpdateStatus status, string detail)
    {
        _lastStatus = status;
        _lastStatusDetail = detail;
        switch (status)
        {
            case UpdateStatus.Failed:
                _logger?.Warning("[BgutilPluginUpdater] " + detail);
                break;
            case UpdateStatus.Updated:
                _logger?.Success("[BgutilPluginUpdater] " + detail);
                break;
            default:
                _logger?.Info("[BgutilPluginUpdater] " + detail);
                break;
        }
        try { OnStatusChanged?.Invoke(status, detail, _localSha, _remoteSha); }
        catch { /* UI dispatch failures must not break the update pipeline */ }
    }

    private string ReadLocalSha()
    {
        try
        {
            if (!File.Exists(_versionFile)) return "";
            return File.ReadAllText(_versionFile).Trim();
        }
        catch { return ""; }
    }

    private async Task<string> FetchLatestShaAsync()
    {
        _lastFetchError = "";
        try
        {
            using var resp = await _http.GetAsync(LatestCommitUrl);
            if (!resp.IsSuccessStatusCode)
            {
                _lastFetchError = "HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase;
                return "";
            }
            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("sha", out var sha))
            {
                string full = sha.GetString() ?? "";
                // Match the short-form SHA build.ps1 writes, so cached-vs-remote comparisons line up.
                return full.Length >= 7 ? full.Substring(0, 7) : full;
            }
            _lastFetchError = "Response JSON missing 'sha' field.";
        }
        catch (Exception ex)
        {
            _lastFetchError = ex.GetType().Name + ": " + ex.Message;
        }
        return "";
    }

    // Download zipball → extract yt_dlp_plugins/ subtree → atomic-swap over tools/yt-dlp-plugins/yt_dlp_plugins.
    // Returns the SHA that was installed, or null on failure. The old tree is kept as a `.bak`
    // sibling until the swap proves clean so we can roll back on extraction failures.
    private async Task<string?> DownloadAndSwapAsync(string assetUrl, string sha)
    {
        string tmpRoot = Path.Combine(Path.GetTempPath(), "wkvrcproxy_bgutil_" + Guid.NewGuid().ToString("N"));
        string? zipPath = null;
        try
        {
            Directory.CreateDirectory(tmpRoot);
            zipPath = Path.Combine(tmpRoot, "bgutil.zip");

            using (var resp = await _http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.Debug("[BgutilPluginUpdater] Download HTTP " + (int)resp.StatusCode);
                    return null;
                }
                using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs);
            }

            string extractDir = Path.Combine(tmpRoot, "extracted");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // GitHub zipballs wrap everything in a "{repo}-{sha}/" top-level folder. Find it
            // rather than assume the exact name — `sha` above is 7-char, the folder uses the
            // short SHA GitHub picks and isn't always byte-identical to our trimmed string.
            string? repoRoot = null;
            foreach (var dir in Directory.EnumerateDirectories(extractDir))
            {
                if (Directory.Exists(Path.Combine(dir, "plugin", "yt_dlp_plugins")))
                {
                    repoRoot = dir;
                    break;
                }
            }
            if (repoRoot == null)
            {
                _logger?.Warning("[BgutilPluginUpdater] Downloaded zipball did not contain plugin/yt_dlp_plugins — bgutil layout may have changed.");
                return null;
            }

            string newPluginDir = Path.Combine(repoRoot, "plugin", "yt_dlp_plugins");
            // yt-dlp requires the <plugin-root>/<PACKAGE-NAME>/yt_dlp_plugins/ layout —
            // without the intermediate package folder yt-dlp silently reports "Plugin directories:
            // none" and the bgutil PO provider never registers.
            string livePackageDir = Path.Combine(_pluginRootDir, "bgutil-ytdlp-pot-provider");
            string livePluginDir = Path.Combine(livePackageDir, "yt_dlp_plugins");
            string backupDir = livePluginDir + ".old";

            Directory.CreateDirectory(livePackageDir);

            // Roll the live tree into a .old backup, move the freshly-extracted tree into place,
            // then drop the backup. If the move fails mid-way, the backup is our rollback path —
            // restore-on-error keeps the user's install functional even on a broken download.
            TryDeleteTree(backupDir);
            if (Directory.Exists(livePluginDir))
            {
                Directory.Move(livePluginDir, backupDir);
            }
            try
            {
                Directory.Move(newPluginDir, livePluginDir);
            }
            catch
            {
                // Restore the old tree on failure.
                if (Directory.Exists(backupDir) && !Directory.Exists(livePluginDir))
                {
                    try { Directory.Move(backupDir, livePluginDir); } catch { }
                }
                throw;
            }
            TryDeleteTree(backupDir);

            File.WriteAllText(_versionFile, sha);
            return sha;
        }
        catch (Exception ex)
        {
            _logger?.Warning("[BgutilPluginUpdater] Install error: " + ex.Message);
            return null;
        }
        finally
        {
            try { if (zipPath != null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            TryDeleteTree(tmpRoot);
        }
    }

    private static void TryDeleteTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    public ModuleHealthReport GetHealthReport()
    {
        return _lastStatus switch
        {
            UpdateStatus.Failed => new ModuleHealthReport {
                ModuleName = Name,
                Status = HealthStatus.Degraded,
                Reason = "bgutil plugin update: " + _lastStatusDetail,
                LastChecked = DateTime.Now
            },
            UpdateStatus.UpdateAvailable => new ModuleHealthReport {
                ModuleName = Name,
                Status = HealthStatus.Degraded,
                Reason = "bgutil plugin update pending (" +
                         (string.IsNullOrEmpty(_localSha) ? "(none)" : _localSha) + " → " + _remoteSha + ")",
                LastChecked = DateTime.Now
            },
            _ => new ModuleHealthReport {
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
