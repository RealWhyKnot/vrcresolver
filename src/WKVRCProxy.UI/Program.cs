using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Photino.NET;
using WKVRCProxy.Core;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Services;
using WKVRCProxy.Core.IPC;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace WKVRCProxy.UI;

[SupportedOSPlatform("windows")]
class Program
{
    private static PhotinoWindow? _window;
    private static SettingsManager? _settings;
    private static Logger? _logger;
    private static ModuleCoordinator? _coordinator;
    private static WhyKnotShareService? _shareService;
    private static ResolutionEngine? _resEngine;
    private static ReportingService? _reportingService;
    private static BrowserExtractService? _browserExtractor;
    private static bool _isWindowReady = false;

    // Single-instance guard. Held for the lifetime of the process; releasing it on shutdown
    // lets a fresh instance start immediately. Local\ scope means per-Windows-session — a user
    // logged in twice could still run two copies, but the common "clicked the shortcut twice"
    // and "VRChat relaunched us" cases are both blocked.
    private static Mutex? _singleInstanceMutex;
    private const string SingleInstanceMutexName = "Local\\WKVRCProxy.UI.SingleInstance";

    // Clean-shutdown marker. Written at the end of OnShutdown() once restore + cleanup succeed,
    // checked at startup. Missing or stale-PID flag = previous run crashed → run recovery before
    // re-patching. Lives in %LOCALAPPDATA%\WKVRCProxy so an install-folder swap (updater) doesn't
    // erase it — recovery state must survive an in-place upgrade.
    private static string CleanExitFlagPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WKVRCProxy", "clean_exit.flag");

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    private const int SW_RESTORE = 9;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--setup-hosts")
        {
            SetupHostsBypass();
            return;
        }

        // Single-instance check BEFORE any state is touched. If a copy is already running,
        // try to focus its window instead of silently exiting so the user understands why the
        // second click "did nothing".
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                var current = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(current.ProcessName))
                {
                    if (p.Id == current.Id) continue;
                    IntPtr h = p.MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        ShowWindow(h, SW_RESTORE);
                        SetForegroundWindow(h);
                        break;
                    }
                }
            }
            catch { /* best-effort focus; any failure falls through to the message box */ }
            MessageBox.Show(
                "WKVRCProxy is already running.\n\nCheck your taskbar — the existing window has been brought to the front.",
                "WKVRCProxy",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                string crashLog = Path.Combine(baseDir, "crash.log");
                File.WriteAllText(crashLog, "FATAL: " + e.ExceptionObject.ToString());
            };

            RunApp(baseDir);
        }
        catch (Exception ex)
        {
            string crashLog = Path.Combine(baseDir, "startup_crash.log");
            File.WriteAllText(crashLog, "STARTUP ERROR: " + ex.ToString());
            MessageBox.Show("Fatal error during startup.\n\nSee startup_crash.log for details.", "WKVRCProxy", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void RunApp(string baseDir)
    {
        _settings = new SettingsManager(baseDir);
        _logger = new Logger(baseDir, "System", _settings);
        _settings.SetLogger(_logger); // inject after construction — breaks circular dep

        _coordinator = new ModuleCoordinator(_logger, _settings);

        // Wire logger into the centralized event bus
        _logger.SetEventBus(_coordinator.EventBus);

        var logMonitor = new VrcLogMonitor();
        var codecInstaller = new CodecInstaller();
        var patcherService = new PatcherService();
        var ipcServer = new WebSocketIpcServer();
        var tier2Client = new Tier2WebSocketClient(_logger);
        var hostsManager = new HostsManager();
        var relayPortManager = new RelayPortManager();
        var proxyRuleManager = new ProxyRuleManager();
        var relayServer = new RelayServer();
        var curlClient = new CurlImpersonateClient();
        var potProvider = new PotProviderService();
        var integrityManager = new RelayIntegrityManager();
        var ytDlpUpdater = new YtDlpUpdater();
        var bgutilPluginUpdater = new BgutilPluginUpdater();
        var appUpdateChecker = new AppUpdateChecker();
        var browserSessionCache = new BrowserSessionCache();
        // BrowserExtractService is not a module (no InitializeAsync lifecycle needed — its browser
        // is lazy-initialised on first extraction call). Constructed here so Program.cs owns its
        // dispose lifecycle and it can be passed into ResolutionEngine by constructor.
        var warpService = new WarpService();
        var browserExtractService = new BrowserExtractService(_logger, _settings, browserSessionCache, warpService);
        _browserExtractor = browserExtractService;

        _coordinator.Register(logMonitor);
        _coordinator.Register(codecInstaller);
        _coordinator.Register(patcherService);
        _coordinator.Register(ipcServer);
        _coordinator.Register(tier2Client);
        _coordinator.Register(hostsManager);
        _coordinator.Register(relayPortManager);
        _coordinator.Register(proxyRuleManager);
        _coordinator.Register(curlClient);
        _coordinator.Register(potProvider);
        _coordinator.Register(browserSessionCache);  // must register before RelayServer — it depends on this
        _coordinator.Register(relayServer);
        _coordinator.Register(integrityManager);
        _coordinator.Register(ytDlpUpdater);
        _coordinator.Register(bgutilPluginUpdater);
        _coordinator.Register(appUpdateChecker);
        _coordinator.Register(warpService);

        ytDlpUpdater.OnStatusChanged += (status, detail, local, remote) => {
            SendToUi("YTDLP_UPDATE", new {
                status = status.ToString(),
                detail,
                localVersion = local,
                remoteVersion = remote
            });
        };

        appUpdateChecker.OnStatusChanged += (status, detail, local, remote, releaseUrl, downloadUrl) => {
            SendToUi("APP_UPDATE", new {
                status = status.ToString(),
                detail,
                localVersion = local,
                remoteVersion = remote,
                releaseUrl,
                downloadUrl
            });
        };

        // Keep legacy event handlers for backward compatibility during transition.
        // These will be removed once all UI communication moves through the event bus.
        hostsManager.OnIpcRequest += (type, data) => {
            if (_isWindowReady) {
                try {
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = type, data = data }));
                    });
                } catch (Exception ex) {
                    _logger?.Warning("IPC send to UI failed: " + ex.Message, ex);
                }
            }
            else {
                Task.Run(async () => {
                    while (!_isWindowReady) await Task.Delay(200);
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = type, data = data }));
                    });
                });
            }
        };

        relayServer.OnRelayEvent += (relayEvent) => {
            if (_isWindowReady) {
                try {
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "RELAY_EVENT", data = relayEvent }));
                    });
                } catch (Exception ex) {
                    _logger?.Warning("RelayEvent send to UI failed: " + ex.Message, ex);
                }
            }
        };

        _shareService = new WhyKnotShareService(_logger);

        _shareService.OnPublicUrlReady += (publicUrl) => {
            SendToUi("P2P_SHARE_STARTED", new { publicUrl });
        };
        _shareService.OnStopped += () => {
            SendToUi("P2P_SHARE_STOPPED", null);
        };
        _shareService.OnError += (message) => {
            SendToUi("P2P_SHARE_ERROR", new { message });
        };

        // Anonymous reporting: opt-in, fires on end-of-cascade failure, sanitizes everything
        // before transmission. Reads the build's version.txt (same source as AppUpdateChecker)
        // for the appVersion field; falls back to "unknown" if the file is missing (dev runs).
        string reportingAppVersion = "unknown";
        try
        {
            string vpath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
            if (File.Exists(vpath)) reportingAppVersion = File.ReadAllText(vpath).Trim();
        }
        catch { }
        _reportingService = new ReportingService(_logger, _settings, reportingAppVersion, _coordinator.EventBus);

        _resEngine = new ResolutionEngine(_logger, _settings, logMonitor, tier2Client, hostsManager, relayPortManager, patcherService, curlClient, potProvider, browserExtractService, warpService, _reportingService);
        _resEngine.SetEventBus(_coordinator.EventBus);
        _resEngine.AttachRelayAbortDetector(relayServer);

        ipcServer.OnResolveRequested += async (payload) => await _resEngine.ResolveAsync(payload);
        logMonitor.OnVrcPathDetected += (path) => {
            patcherService.UpdateToolsDir(path);
            ipcServer.ExportPortToDirectory(path);
        };

        _resEngine.OnStatusUpdate += (msg, stats) => {
            if (_isWindowReady) {
                try {
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "STATUS", data = new { message = msg, stats = stats } }));
                    });
                } catch (Exception ex) {
                    _logger?.Warning("Status event send to UI failed: " + ex.Message, ex);
                }
            }
        };

        // Centralized event bus subscription — single point for all events to UI
        _coordinator.EventBus.OnEvent += (evt) => {
            if (!_isWindowReady) return;
            try {
                string eventType;
                object data;
                switch (evt.Type) {
                    case SystemEventType.Health:
                        eventType = "HEALTH";
                        data = evt.Payload!;
                        break;
                    case SystemEventType.Error:
                        eventType = "ERROR";
                        data = evt.Payload!;
                        break;
                    case SystemEventType.StrategyDemoted:
                        eventType = "STRATEGY_DEMOTED";
                        data = evt.Payload!;
                        break;
                    case SystemEventType.Prompt:
                        // PublishPrompt wraps as { type, data }. Forward as PROMPT so the UI store
                        // can dispatch on inner type (anonymousReportingOptIn, etc.).
                        eventType = "PROMPT";
                        data = evt.Payload!;
                        break;
                    default:
                        return; // Log, Status, Relay handled by legacy events for now
                }
                _window?.Invoke(() => {
                    _window?.SendWebMessage(JsonSerializer.Serialize(
                        new { type = eventType, data = data, correlationId = evt.CorrelationId, source = evt.SourceModule }));
                });
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("Event bus -> UI dispatch error: " + ex.Message);
            }
        };

        string webViewDataPath = Path.Combine(baseDir, "WebView2_Data");
        if (!Directory.Exists(webViewDataPath)) Directory.CreateDirectory(webViewDataPath);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewDataPath);

        _window = new PhotinoWindow()
            .SetTitle("WKVRCProxy")
            .SetUseOsDefaultSize(false)
            .SetSize(1200, 800)
            .Center()
            .SetResizable(true)
            .RegisterWebMessageReceivedHandler((s, m) => HandleWebMessage(m))
            .SetLogVerbosity(0);

        Logger.OnLog += (entry) => {
            if (_isWindowReady) {
                try {
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "LOG", data = entry }));
                    });
                } catch { }
            }
        };

        _window.RegisterWindowCreatedHandler((s, a) => {
            Task.Run(async () => {
                try {
                    RunPreflightChecks(baseDir);
                    await _coordinator.InitializeAllAsync();
                    // If VRChat Tools path was already known at startup, export the IPC port
                    // there immediately so the redirector can find it without needing a log event.
                    string? knownToolsDir = patcherService.VrcToolsDir;
                    if (!string.IsNullOrEmpty(knownToolsDir))
                        ipcServer.ExportPortToDirectory(knownToolsDir);

                    // Crash-recovery sweep: if the previous run didn't write a clean-exit flag
                    // we must restore yt-dlp before patching again, otherwise the redirector
                    // ends up patched over itself with no -og backup to revert to.
                    string redirectorPath = Path.Combine(baseDir, "tools", "redirector.exe");
                    if (!WasLastShutdownClean())
                        patcherService.RecoverFromUncleanShutdown(redirectorPath);
                    InvalidateCleanExitFlag();

                    if (_settings.Config.AutoPatchOnStart) {
                        string wrapperPath = Path.Combine(baseDir, "tools", "redirector.exe");
                        if (File.Exists(wrapperPath)) patcherService.StartMonitoring(wrapperPath);
                    }

                    // Mask IP eagerly spins up wireproxy so the first request after launch doesn't
                    // eat the cold-start latency. Failures still leave the service in Failed state;
                    // strategies will refuse to run rather than leak the real IP.
                    if (_settings.Config.MaskIp) {
                        _ = Task.Run(async () => {
                            try { await warpService.EnsureRunningAsync(); }
                            catch (Exception ex) { _logger.Warning("[Warp] Eager start (Mask IP) failed: " + ex.Message); }
                        });
                    }

                    // Start periodic health broadcast (every 10 seconds)
                    _ = Task.Run(async () => {
                        while (true)
                        {
                            await Task.Delay(10000);
                            if (_isWindowReady && _coordinator != null)
                            {
                                try
                                {
                                    var health = _coordinator.GetSystemHealth();
                                    foreach (var report in health)
                                        _coordinator.EventBus.PublishHealth(report);
                                }
                                catch { /* Health check itself shouldn't crash the app */ }
                            }
                        }
                    });
                } catch (Exception ex) {
                    _logger?.Fatal("Coordinator initialization failed: " + ex.Message, ex);
                }
            });
        });

        string indexPath = Path.Combine(baseDir, "wwwroot", "index.html");
        if (!File.Exists(indexPath)) {
            indexPath = Path.GetFullPath(Path.Combine(baseDir, "../../../src/WKVRCProxy.UI/ui/dist/index.html"));
        }

        if (File.Exists(indexPath)) _window.Load(indexPath);
        else _window.LoadRawString("UI Build Missing");

        _window.WaitForClose();
        OnShutdown();
    }

    private static void RunPreflightChecks(string baseDir)
    {
        string toolsDir = Path.Combine(baseDir, "tools");

        string redirector = Path.Combine(toolsDir, "redirector.exe");
        if (!File.Exists(redirector))
        {
            _logger?.Fatal("PREFLIGHT: redirector.exe missing from tools/ — patching is disabled. Reinstall or rebuild.");
            _coordinator?.EventBus.PublishError("Preflight", new ErrorContext {
                Category = ErrorCategory.FileSystem,
                Code = ErrorCodes.REDIRECTOR_MISSING,
                Summary = "redirector.exe missing from tools/",
                Detail = "Patching is disabled without this file",
                ActionHint = "Reinstall or rebuild WKVRCProxy",
                IsRecoverable = false
            });
        }

        string ytdlp = Path.Combine(toolsDir, "yt-dlp.exe");
        if (!File.Exists(ytdlp))
        {
            _logger?.Warning("PREFLIGHT: yt-dlp.exe missing from tools/ — Tier 1 resolution will fail.");
            _coordinator?.EventBus.PublishError("Preflight", new ErrorContext {
                Category = ErrorCategory.FileSystem,
                Code = ErrorCodes.YTDLP_MISSING,
                Summary = "yt-dlp.exe missing from tools/",
                Detail = "Tier 1 resolution will fail without this file",
                ActionHint = "Reinstall or rebuild WKVRCProxy",
                IsRecoverable = true
            });
        }

        string defaultVrcTools = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
            "VRChat", "VRChat", "Tools");
        bool hasCustomPath = !string.IsNullOrEmpty(_settings?.Config.CustomVrcPath);
        if (!Directory.Exists(defaultVrcTools) && !hasCustomPath)
            _logger?.Warning("PREFLIGHT: VRChat Tools folder not found. Launch VRChat at least once, or set a custom path in Settings.");
    }

    private static void OnShutdown()
    {
        bool restoreOk = false;
        try {
            var patcher = _coordinator?.GetModule<PatcherService>();
            patcher?.Shutdown();
            restoreOk = true;
        } catch (Exception ex) {
            _logger?.Warning("Shutdown error: " + ex.Message, ex);
        }

        // Flush bypass memory before the logger and resolver go away — coalesced saves may still
        // be pending from the last few requests.
        try { _resEngine?.StrategyMemory.Flush(); }
        catch (Exception ex) { _logger?.Warning("StrategyMemory flush failed on shutdown: " + ex.Message); }

        _shareService?.Dispose();
        _browserExtractor?.Dispose();
        _coordinator?.Dispose();

        // Write the clean-exit flag only if patcher restore actually ran. Anything else (logger
        // dispose, mutex release) is best-effort housekeeping. If we crash *after* this point the
        // VRChat Tools dir is already in its restored state, so a flag here is honest.
        if (restoreOk) WriteCleanExitFlag();

        _logger?.Dispose();

        // Release the single-instance mutex so the next launch can start immediately.
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* not held or already released */ }
        _singleInstanceMutex?.Dispose();
    }

    private static bool WasLastShutdownClean()
    {
        try { return File.Exists(CleanExitFlagPath); }
        catch { return false; }
    }

    private static void InvalidateCleanExitFlag()
    {
        try
        {
            if (File.Exists(CleanExitFlagPath)) File.Delete(CleanExitFlagPath);
        }
        catch { /* a leftover flag is harmless; it would just defer recovery one launch */ }
    }

    private static void WriteCleanExitFlag()
    {
        try
        {
            string dir = Path.GetDirectoryName(CleanExitFlagPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(CleanExitFlagPath, DateTime.UtcNow.ToString("O") + " pid=" + Environment.ProcessId);
        }
        catch (Exception ex) { _logger?.Debug("Could not write clean-exit flag: " + ex.Message); }
    }

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
    private static void LaunchSidecarAndExit(string exeName, string args = "")
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exePath = Path.Combine(baseDir, exeName);
            if (!File.Exists(exePath))
            {
                _logger?.Error("Cannot launch " + exeName + " — file missing from install dir.");
                SendToUi("SIDECAR_ERROR", new { exe = exeName, message = "File missing from install folder." });
                return;
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
            Process.Start(psi);
            _logger?.Info("Launched " + exeName + "; window will close so file locks release.");
            _window?.Invoke(() => _window?.Close());
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to launch " + exeName + ": " + ex.Message);
            SendToUi("SIDECAR_ERROR", new { exe = exeName, message = ex.Message });
        }
    }

    private static void SetupHostsBypass()
    {
        try
        {
            string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
            File.AppendAllText(hostsPath, "\r\n127.0.0.1 localhost.youtube.com\r\n");

            Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
        }
        catch (Exception ex)
        {
            string crashLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hosts_setup_error.log");
            File.WriteAllText(crashLog, "SETUP ERROR: " + ex.ToString());
        }
        Environment.Exit(0);
    }

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
                case "LAUNCH_UNINSTALLER":
                    LaunchSidecarAndExit("uninstall.exe");
                    break;
            }
        } catch (Exception ex) {
            _logger?.Warning("WebMessage parse error: " + ex.Message, ex);
        }
    }
}
