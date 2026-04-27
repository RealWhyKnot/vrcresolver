using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.WindowsAPICodePack.Dialogs;
using Photino.NET;
using WKVRCProxy.Core;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;
using WKVRCProxy.Core.Services;

namespace WKVRCProxy.UI;

// Partial — UI message dispatch + sidecar launch. Everything that runs in response to a webview
// SAVE_CONFIG/LAUNCH_UPDATER/etc. message lives here, plus the "push update" helpers SendToUi /
// SendBypassMemory / SendYtDlpUpdateStatus / SendAppUpdateStatus that the modules call back into.
// Program.cs is the entry/lifecycle file; nothing here is invoked at startup.
[SupportedOSPlatform("windows")]
partial class Program
{
    // --- moved from Program.cs (lines 500-635) ---
    private static void SendToUi(string type, object? data)
    {
        if (!_isWindowReady) return;
        try {
            _window?.Invoke(() => {
                _window?.SendWebMessage(JsonSerializer.Serialize(new { type, data }));
            });
        } catch (Exception ex) {
            _logger?.Warning("SendToUi '" + type + "' failed: " + ex.Message);
        }
    }

    private static void SendBypassMemory()
    {
        if (_resEngine == null) return;
        var snapshot = _resEngine.StrategyMemory.Snapshot();
        // Flatten to an array the UI can render without key gymnastics.
        var rows = snapshot.Select(kvp => new {
            key = kvp.Key,
            entries = kvp.Value.Select(e => new {
                strategy = e.StrategyName,
                successCount = e.SuccessCount,
                failureCount = e.FailureCount,
                consecutiveFailures = e.ConsecutiveFailures,
                netScore = e.NetScore,
                lastSuccess = e.LastSuccess,
                lastFailure = e.LastFailure,
                firstSeen = e.FirstSeen
            }).ToArray()
        }).ToArray();
        SendToUi("BYPASS_MEMORY", rows);
    }

    private static void SendYtDlpUpdateStatus()
    {
        var updater = _coordinator?.GetModule<YtDlpUpdater>();
        if (updater == null) return;
        SendToUi("YTDLP_UPDATE", new {
            status = updater.Status.ToString(),
            detail = updater.StatusDetail,
            localVersion = updater.LocalVersion,
            remoteVersion = updater.RemoteVersion
        });
    }

    private static void SendAppUpdateStatus()
    {
        var checker = _coordinator?.GetModule<AppUpdateChecker>();
        if (checker == null) return;
        SendToUi("APP_UPDATE", new {
            status = checker.Status.ToString(),
            detail = checker.StatusDetail,
            localVersion = checker.LocalVersion,
            remoteVersion = checker.RemoteVersion,
            releaseUrl = checker.ReleaseUrl,
            downloadUrl = checker.DownloadUrl
        });
    }

    // Spawns updater.exe / uninstall.exe sitting next to WKVRCProxy.exe in the install dir,
    // then closes the window so the spawned process can take over file locks. Both are signed
    // single-file exes that handle their own work; we just need to get out of their way.
    //
    // forceElevation=true is the SmartScreen / UAC fallback path the UI hits after a normal
    // launch returned ERROR_CANCELLED: we strip Mark-of-the-Web from the sidecar (so SmartScreen
    // stops gating) then re-launch with Verb=runas (explicit admin request). The user gets a
    // clearer UAC prompt and a cleaner code path; if they decline again, we report the same way.
    private static void LaunchSidecarAndExit(string exeName, string args = "", bool forceElevation = false)
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exePath = Path.Combine(baseDir, exeName);
            if (!File.Exists(exePath))
            {
                _logger?.Error("Cannot launch " + exeName + " — file missing from install dir.");
                SendToUi("SIDECAR_ERROR", new { exe = exeName, message = "File missing from install folder.", canForce = false });
                return;
            }

            if (forceElevation)
            {
                // Best-effort: remove the Mark-of-the-Web alternate stream so SmartScreen no longer
                // treats the file as untrusted-from-internet. PowerShell's Unblock-File handles
                // this without needing admin (the user owns the file). Failures are non-fatal —
                // if the stream isn't there or PowerShell isn't available, we just skip and let
                // the elevated launch proceed.
                try
                {
                    using var ps = Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Unblock-File -Path '" + exePath.Replace("'", "''") + "'\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    ps?.WaitForExit(5000);
                    _logger?.Debug("Unblock-File ran on " + exePath + " (exit " + (ps?.ExitCode ?? -1) + ").");
                }
                catch (Exception ubex)
                {
                    _logger?.Debug("Unblock-File skipped: " + ubex.Message);
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.IsNullOrEmpty(args)
                    ? "--install-dir \"" + baseDir.TrimEnd('\\') + "\""
                    : args,
                UseShellExecute = true,
                WorkingDirectory = baseDir
            };
            if (forceElevation) psi.Verb = "runas";

            Process.Start(psi);
            _logger?.Info("Launched " + exeName + (forceElevation ? " (force-elevation)" : "") + "; window will close so file locks release.");
            _window?.Invoke(() => _window?.Close());
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to launch " + exeName + ": " + ex.Message);
            // canForce=true lets the UI offer the "Try with elevation" button only on the first
            // attempt — once forceElevation was already tried, falling back to the same retry
            // path would just re-prompt UAC the user already declined.
            SendToUi("SIDECAR_ERROR", new {
                exe = exeName,
                message = ex.Message,
                canForce = !forceElevation
            });
        }
    }


    // --- moved from Program.cs (lines 653-875) ---
    private static void HandleWebMessage(string message)
    {
        _isWindowReady = true; // UI is now alive and safe to receive messages
        try {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";

            switch (type) {
                case "EXIT": _window?.Close(); break;
                case "GET_CONFIG": _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "CONFIG", data = _settings?.Config })); break;
                case "OPEN_BROWSER":
                    if (root.TryGetProperty("data", out var browserData)) {
                        string url = browserData.GetProperty("url").GetString() ?? "";
                        if (!string.IsNullOrEmpty(url)) Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                    }
                    break;
                case "SYNC_LOGS":
                    foreach (var entry in Logger.GetHistory())
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "LOG", data = entry }));
                    break;
                case "SAVE_CONFIG":
                    if (root.TryGetProperty("data", out var configData)) {
                        var newConfig = JsonSerializer.Deserialize<WKVRCProxy.Core.Models.AppConfig>(configData.GetRawText());
                        if (newConfig != null && _settings != null) {
                            bool wasMaskIp = _settings.Config.MaskIp;
                            _settings.Config.DebugMode = newConfig.DebugMode;
                            _settings.Config.PreferredResolution = newConfig.PreferredResolution;
                            _settings.Config.ForceIPv4 = newConfig.ForceIPv4;
                            _settings.Config.AutoPatchOnStart = newConfig.AutoPatchOnStart;
                            _settings.Config.CustomVrcPath = newConfig.CustomVrcPath;
                            _settings.Config.BypassHostsSetupDeclined = newConfig.BypassHostsSetupDeclined;
                            _settings.Config.EnableRelayBypass = newConfig.EnableRelayBypass;
                            _settings.Config.EnablePreflightProbe = newConfig.EnablePreflightProbe;
                            _settings.Config.NativeAvProUaHosts = newConfig.NativeAvProUaHosts ?? new List<string> { "vr-m.net" };
                            if (newConfig.StrategyPriority != null)
                            {
                                _settings.Config.StrategyPriority = newConfig.StrategyPriority;
                            }
                            _settings.Config.EnableWaveRace = newConfig.EnableWaveRace;
                            if (newConfig.WaveSize > 0) _settings.Config.WaveSize = newConfig.WaveSize;
                            if (newConfig.WaveStageDeadlineSeconds > 0) _settings.Config.WaveStageDeadlineSeconds = newConfig.WaveStageDeadlineSeconds;
                            if (newConfig.PerHostRequestBudget > 0) _settings.Config.PerHostRequestBudget = newConfig.PerHostRequestBudget;
                            if (newConfig.PerHostRequestWindowSeconds > 0) _settings.Config.PerHostRequestWindowSeconds = newConfig.PerHostRequestWindowSeconds;
                            _settings.Config.MaskIp = newConfig.MaskIp;
                            _settings.Config.EnableAnonymousReporting = newConfig.EnableAnonymousReporting;
                            _settings.Config.AnonymousReportingPromptAnswered = newConfig.AnonymousReportingPromptAnswered;
                            _settings.Config.EnableWebsiteTab = newConfig.EnableWebsiteTab;
                            _settings.Config.UserOverriddenKeys = newConfig.UserOverriddenKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _settings.Save();

                            // Mask-IP off→on: eagerly spin up wireproxy so the next request doesn't
                            // pay the cold-start latency. Failures stay in Failed state; the next
                            // strategy attempt will surface the reason.
                            if (!wasMaskIp && _settings.Config.MaskIp)
                            {
                                var warp = _coordinator?.GetModule<WarpService>();
                                if (warp != null)
                                {
                                    _ = Task.Run(async () => {
                                        try { await warp.EnsureRunningAsync(); }
                                        catch (Exception ex) { _logger?.Warning("[Warp] Eager start (Mask IP toggled on) failed: " + ex.Message); }
                                    });
                                }
                            }
                        }
                    }
                    break;
                case "SET_ANONYMOUS_REPORTING":
                    if (root.TryGetProperty("data", out var arData)
                        && arData.TryGetProperty("optIn", out var optInEl)
                        && (optInEl.ValueKind == JsonValueKind.True || optInEl.ValueKind == JsonValueKind.False))
                    {
                        _reportingService?.RecordUserAnswer(optInEl.GetBoolean());
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "CONFIG", data = _settings?.Config }));
                    }
                    break;
                case "PICK_VRC_PATH":
                    _window?.Invoke(() => {
                        using var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
                        if (dialog.ShowDialog() == CommonFileDialogResult.Ok && _settings != null) {
                            _settings.Config.CustomVrcPath = dialog.FileName;
                            _settings.Save();
                            _coordinator?.GetModule<PatcherService>().UpdateToolsDir(dialog.FileName);
                            _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "CONFIG", data = _settings.Config }));
                        }
                    });
                    break;
                case "HOSTS_SETUP_ACCEPTED":
                    _coordinator?.GetModule<HostsManager>().HandleUserResponse(true);
                    break;
                case "HOSTS_SETUP_DECLINED":
                    _coordinator?.GetModule<HostsManager>().HandleUserResponse(false);
                    break;
                case "REQUEST_HOSTS_SETUP":
                    Task.Run(() => {
                        _coordinator?.GetModule<HostsManager>().RequestBypassAsync();
                    });
                    break;
                case "ADD_FIREWALL_RULE":
                    Task.Run(() => {
                        try
                        {
                            var psi = new ProcessStartInfo {
                                FileName = "netsh",
                                Arguments = "advfirewall firewall add rule name=\"WKVRCProxy Relay\" dir=in action=allow program=\"" + Process.GetCurrentProcess().MainModule?.FileName + "\" enable=yes",
                                Verb = "runas",
                                UseShellExecute = true,
                                WindowStyle = ProcessWindowStyle.Hidden
                            };
                            Process.Start(psi)?.WaitForExit();
                            _logger?.Success("Firewall exclusion rule added successfully.");
                        }
                        catch (Exception ex)
                        {
                            _logger?.Error("Failed to add firewall rule: " + ex.Message);
                        }
                    });
                    break;
                case "START_P2P_SHARE":
                    if (root.TryGetProperty("data", out var shareData)) {
                        string shareUrl = shareData.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(shareUrl)) {
                            Task.Run(async () => {
                                // Resolve the user's URL through the tier cascade first so the P2P relay
                                // sees a direct media URL (not a YouTube watch page).
                                string? resolved = await (_resEngine?.ResolveForShareAsync(shareUrl, "AVPro", "P2PShare")
                                                          ?? Task.FromResult<string?>(null));
                                if (string.IsNullOrEmpty(resolved)) {
                                    SendToUi("P2P_SHARE_ERROR", new { message = "Failed to resolve URL through tier cascade." });
                                    return;
                                }
                                if (_shareService != null) await _shareService.StartAsync(resolved);
                            });
                        }
                    }
                    break;
                case "STOP_P2P_SHARE":
                    _shareService?.Stop();
                    break;
                case "REQUEST_CLOUD_RESOLVE":
                    if (root.TryGetProperty("data", out var cloudData)) {
                        string cloudUrl = cloudData.TryGetProperty("url", out var cuEl) ? cuEl.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(cloudUrl)) {
                            Task.Run(async () => {
                                string? resolved = await (_resEngine?.ResolveForShareAsync(cloudUrl, "AVPro", "CloudShare")
                                                          ?? Task.FromResult<string?>(null));
                                if (string.IsNullOrEmpty(resolved)) {
                                    SendToUi("CLOUD_RESOLVE_RESULT", new { success = false, message = "Failed to resolve URL through tier cascade." });
                                    return;
                                }
                                // Find the history entry we just created to surface resolution + tier info
                                var entry = _settings?.Config.History.Count > 0 ? _settings.Config.History[0] : null;
                                SendToUi("CLOUD_RESOLVE_RESULT", new {
                                    success = true,
                                    url = resolved,
                                    tier = entry?.Tier,
                                    height = entry?.ResolutionHeight,
                                    width = entry?.ResolutionWidth
                                });
                            });
                        }
                    }
                    break;
                case "GET_HEALTH":
                    if (_coordinator != null)
                    {
                        var health = _coordinator.GetSystemHealth();
                        _window?.SendWebMessage(JsonSerializer.Serialize(new {
                            type = "HEALTH",
                            data = health,
                            overall = _coordinator.GetOverallHealth().ToString()
                        }));
                    }
                    break;
                case "GET_BYPASS_MEMORY":
                    SendBypassMemory();
                    break;
                case "FORGET_BYPASS_KEY":
                    if (root.TryGetProperty("data", out var forgetData) &&
                        forgetData.TryGetProperty("key", out var forgetKey))
                    {
                        string key = forgetKey.GetString() ?? "";
                        if (!string.IsNullOrEmpty(key) && _resEngine != null)
                        {
                            _resEngine.StrategyMemory.ForgetKey(key);
                            _logger?.Info("[BypassMemory] Forgot '" + key + "' on user request.");
                            SendBypassMemory();
                        }
                    }
                    break;
                case "GET_YTDLP_UPDATE":
                    SendYtDlpUpdateStatus();
                    break;
                case "GET_APP_UPDATE":
                    SendAppUpdateStatus();
                    break;
                case "APP_UPDATE_CHECK":
                    Task.Run(async () => {
                        var checker = _coordinator?.GetModule<AppUpdateChecker>();
                        if (checker != null) await checker.CheckAsync();
                    });
                    break;
                case "LAUNCH_UPDATER":
                    LaunchSidecarAndExit("updater.exe");
                    break;
                case "LAUNCH_UPDATER_FORCE":
                    // SmartScreen / UAC bypass path — Unblock-File first, then re-launch with
                    // Verb=runas so Windows shows an explicit admin elevation prompt instead of
                    // its installer-detection auto-UAC.
                    LaunchSidecarAndExit("updater.exe", forceElevation: true);
                    break;
                case "LAUNCH_UNINSTALLER":
                    LaunchSidecarAndExit("uninstall.exe");
                    break;
                case "LAUNCH_UNINSTALLER_FORCE":
                    LaunchSidecarAndExit("uninstall.exe", forceElevation: true);
                    break;
                case "GET_CHANGELOG":
                    // Streams the embedded CHANGELOG.md back as raw markdown; the UI
                    // (App.vue → marked) renders it. Embedding is set in WKVRCProxy.UI.csproj
                    // (<EmbeddedResource Include="..\..\CHANGELOG.md" LogicalName="...">) —
                    // keep the resource name in sync with that.
                    try {
                        string content;
                        using (var stream = Assembly.GetExecutingAssembly()
                            .GetManifestResourceStream("WKVRCProxy.UI.CHANGELOG.md"))
                        {
                            if (stream == null) {
                                content = "Changelog unavailable in this build.";
                            } else {
                                using var reader = new StreamReader(stream);
                                content = reader.ReadToEnd();
                            }
                        }
                        SendToUi("CHANGELOG", new { content });
                    } catch (Exception ex) {
                        _logger?.Warning("GET_CHANGELOG failed: " + ex.Message, ex);
                        SendToUi("CHANGELOG", new { content = "Failed to load changelog: " + ex.Message });
                    }
                    break;
            }
        } catch (Exception ex) {
            _logger?.Warning("WebMessage parse error: " + ex.Message, ex);
        }
    }

}
