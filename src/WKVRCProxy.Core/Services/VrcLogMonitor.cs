using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class VrcLogMonitor : IProxyModule, IDisposable
{
    public string Name => "LogMonitor";
    private Logger? _logger;
    private SettingsManager? _settings;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private Task? _redirectorLogTask;
    private bool _vrcToolsDetected = false;

    public string CurrentPlayer { get; private set; } = "AVPro";
    public event Action<string>? OnVrcPathDetected;

    // Fires when VRChat logs "[AVProVideo] Error: Loading failed" within a short window after an
    // "[AVProVideo] Opening <url>" line. The URL is the one AVPro tried to open (pre-resolution:
    // the original user URL, not our resolved URL). Subscribers use this to demote the strategy
    // that produced the URL and invalidate any cache entry keyed on it. Second arg is the timestamp
    // the failure line was observed (UTC).
    public event Action<string, DateTime>? OnAvProLoadFailure;

    // Fires when an "[AVProVideo] Opening <url>" line is NOT followed by either a "Loading failed"
    // line or a success indicator ("Now Playing:" / "Load Url:") within SilentStallWindow. This is
    // the missing fourth observability signal — the case where AVPro silently does nothing after
    // accepting a URL (no error, no playback, world script may or may not retry). Subscribers
    // treat this the same as a Loading-failed event: demote the strategy that produced the URL,
    // evict any resolve-cache entry, force a fresh cascade on the next request.
    public event Action<string, DateTime>? OnAvProSilentStall;

    // Correlation state for the Opening → Error pattern. We only keep the most recent Opening URL
    // and its observation time; a "Loading failed" within CorrelationWindow demotes. Older Openings
    // are cleared on state transitions.
    private string? _lastAvProOpeningUrl;
    private DateTime _lastAvProOpeningAt;
    private static readonly TimeSpan AvProOpenFailCorrelationWindow = TimeSpan.FromSeconds(10);
    private static readonly Regex _avProOpeningRegex = new(
        @"\[AVProVideo\] Opening\s+(\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Silent-stall watchdog. SilentStallWindow > AvProOpenFailCorrelationWindow so a slow
    // Loading-failed line still wins the race and routes through OnAvProLoadFailure (avoids
    // double-firing both signals for the same incident). 12 s gives AVPro plenty of headroom for
    // first-frame on a slow upstream while still feeling responsive — the user has been staring
    // at a loading screen for 12 s by the time we conclude something is wrong.
    private static readonly TimeSpan SilentStallWindow = TimeSpan.FromSeconds(12);
    private CancellationTokenSource? _silentStallCts;
    private string? _silentStallUrl;
    private readonly object _silentStallLock = new();

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _settings = context.Settings;
        _logger.Info("Starting VRChat Log Monitor...");
        _monitorTask = Task.Run(MonitorLoop);
        _redirectorLogTask = Task.Run(TailRedirectorLog);
        return Task.CompletedTask;
    }

    public void Shutdown()
    {
        _cts.Cancel();
        // Cancel any pending silent-stall watchdog so it doesn't fire post-shutdown with a stale
        // event subscriber list. Cheap to call and idempotent if no watchdog is active.
        CancelSilentStallWatchdog();
    }
    
    private async Task MonitorLoop()
    {
        string vrcDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", "VRChat", "VRChat");
        string currentFile = "";
        long lastSize = 0;
        
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!Directory.Exists(vrcDir))
                {
                    await Task.Delay(5000, _cts.Token);
                    continue;
                }

                string localTools = Path.Combine(vrcDir, "Tools");
                if (Directory.Exists(localTools) && !_vrcToolsDetected)
                {
                    _vrcToolsDetected = true;
                    OnVrcPathDetected?.Invoke(localTools);
                }
                
                var latestLog = new DirectoryInfo(vrcDir)
                    .GetFiles("output_log*.txt")
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();
                    
                if (latestLog != null)
                {
                    if (latestLog.FullName != currentFile)
                    {
                        currentFile = latestLog.FullName;
                        lastSize = 0;
                        _logger?.Info("Tracking new VRChat log: " + currentFile);
                    }
                    
                    if (latestLog.Length > lastSize)
                    {
                        using (var fs = new FileStream(currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            fs.Seek(lastSize, SeekOrigin.Begin);
                            using (var reader = new StreamReader(fs))
                            {
                                string newContent = await reader.ReadToEndAsync();
                                lastSize = fs.Position;
                                
                                if (newContent.Contains("Application Path:"))
                                {
                                    var match = Regex.Match(newContent, @"Application Path:\s*(.+)");
                                    if (match.Success)
                                    {
                                        string exePath = match.Groups[1].Value.Trim();
                                        _logger?.Debug("VRChat Application Path detected: " + exePath);
                                        string? toolsDir = DetectToolsFromExe(exePath);
                                        if (toolsDir != null) OnVrcPathDetected?.Invoke(toolsDir);
                                    }
                                }

                                if (newContent.Contains("Video component initialization: Unity") || newContent.Contains("UnityVideoPlayer"))
                                {
                                    if (CurrentPlayer != "Unity")
                                    {
                                        CurrentPlayer = "Unity";
                                        _logger?.Info("Player Engine Switch Detected: Unity Video Player");
                                    }
                                }
                                else if (newContent.Contains("Video component initialization: AVPro") || newContent.Contains("AVProVideo"))
                                {
                                    if (CurrentPlayer != "AVPro")
                                    {
                                        CurrentPlayer = "AVPro";
                                        _logger?.Info("Player Engine Switch Detected: AVPro Video Player");
                                    }
                                }

                                ForwardVrcLogLines(newContent);
                            }
                        }
                    }
                }
                
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.Warning("VrcLogMonitor Error: " + ex.Message, ex);
                await Task.Delay(5000, _cts.Token);
            }
        }
    }

    // Tail the Redirector's yt-dlp-wrapper.log from the VRChat Tools directory
    // so connection failures and resolution results are visible in the main logger.
    private async Task TailRedirectorLog()
    {
        string? logPath = null;
        long lastSize = 0;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, _cts.Token);

                // Discover the log path from the VRChat Tools directory (where the Redirector writes)
                if (logPath == null || !File.Exists(logPath))
                {
                    string vrcDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", "VRChat", "VRChat", "Tools");
                    string candidate = Path.Combine(vrcDir, "yt-dlp-wrapper.log");
                    if (File.Exists(candidate))
                    {
                        if (logPath != candidate)
                        {
                            logPath = candidate;
                            // Start from current end so we don't replay old entries
                            try { lastSize = new FileInfo(logPath).Length; } catch { lastSize = 0; }
                        }
                    }
                    else
                    {
                        continue;
                    }
                }

                long currentSize = new FileInfo(logPath).Length;
                if (currentSize <= lastSize)
                {
                    if (currentSize < lastSize) lastSize = 0;
                    continue;
                }

                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(lastSize, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                string newContent = await reader.ReadToEndAsync();
                lastSize = fs.Position;

                foreach (string rawLine in newContent.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    // Failures get Warning level so they're clearly visible
                    LogLevel level = line.Contains("FAIL:") ? LogLevel.Warning : LogLevel.Info;
                    _logger?.LogWithSource(level, "Redirector", line);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* File locked or inaccessible — retry next cycle */ }
        }
    }

    private void ForwardVrcLogLines(string content)
    {
        if (_logger == null) return;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Only forward lines that directly help diagnose video resolution issues.
            // Excluded: AVPro init spam, VideoTXL play/stop/loop events, player registrations,
            // "No MediaReference" errors, JSON stats blobs, MovieCapture init, etc.
            bool isRelevant = line.Contains("NativeProcess.Start:")
                           || line.Contains("NativeProcess.HasExited:")
                           || line.Contains("loading URL by user")
                           || line.Contains("Load Url:")
                           || line.Contains("Now Playing:")
                           || line.Contains("[AVProVideo] Error:")
                           || line.Contains("[AVProVideo] Opening ")
                           || line.Contains("[AVProVideo] Using playback path:")
                           || line.Contains("[VRC.SDK3.Video]")
                           || line.Contains("[VRC.SDK2.Video]");

            // Exclude known noise that matches the above patterns
            if (line.Contains("No MediaReference") || line.Contains("No file path specified"))
                isRelevant = false;

            if (isRelevant)
            {
                _logger?.LogWithSource(LogLevel.Info, "VRChat", line);
            }

            // Track Opening → Error pairing so the resolution engine can demote the strategy that
            // produced a URL AVPro couldn't actually play. The "Opening" line carries the URL;
            // "Error: Loading failed" doesn't — so we match by time proximity.
            var openingMatch = _avProOpeningRegex.Match(line);
            if (openingMatch.Success)
            {
                string openedUrl = openingMatch.Groups[1].Value;
                _lastAvProOpeningUrl = openedUrl;
                _lastAvProOpeningAt = DateTime.UtcNow;
                string shortOpened = openedUrl.Length > 100 ? openedUrl.Substring(0, 100) + "..." : openedUrl;
                _logger?.Info("[Playback] AVPro Opening received → starting watchdog (" + (int)SilentStallWindow.TotalSeconds + "s) for " + shortOpened);
                StartSilentStallWatchdog(openedUrl);
            }
            else if (line.Contains("[AVProVideo] Error: Loading failed"))
            {
                if (_lastAvProOpeningUrl != null
                    && DateTime.UtcNow - _lastAvProOpeningAt <= AvProOpenFailCorrelationWindow)
                {
                    string failedUrl = _lastAvProOpeningUrl;
                    _lastAvProOpeningUrl = null; // consume to avoid double-firing on retry errors
                    string shortFailed = failedUrl.Length > 100 ? failedUrl.Substring(0, 100) + "..." : failedUrl;
                    _logger?.Info("[Playback] AVPro Loading failed (correlated to recent Opening) for " + shortFailed);
                    try { OnAvProLoadFailure?.Invoke(failedUrl, DateTime.UtcNow); }
                    catch (Exception ex) { _logger?.Warning("[VrcLogMonitor] OnAvProLoadFailure handler threw: " + ex.Message); }
                }
                // Loading failed always cancels any pending silent-stall watchdog — the loud-failure
                // path has already fired (or is below the correlation threshold), and we don't want
                // the same incident raising a second signal under a different name.
                CancelSilentStallWatchdog();
            }
            else if (line.Contains("[AVProVideo] Using playback path:"))
            {
                // Universal AVPro success marker — fires after Opening when AVPro picked a backend
                // decoder (MF-MediaEngine-Hardware etc.) and started loading. Cancels the watchdog
                // regardless of which world script wraps AVPro. Without this, worlds that don't use
                // ProTV / VideoTXL (e.g. Five Nights at Freddy's) silent-stall a working playback.
                _logger?.Info("[Playback] AVPro success marker — Using playback path → cancelling silent-stall watchdog (playback started)");
                CancelSilentStallWatchdog();
            }
            else if (line.Contains("Now Playing:") || line.Contains("Load Url:"))
            {
                // World-script success indicators (ProTV / TVManager / VideoTXL). Belt-and-suspenders
                // alongside the AVPro marker — confirms the world's view of playback start. Don't
                // URL-match: the world's URL log names the original user URL, the watchdog is keyed
                // on the wrapped/relayed URL AVPro opened.
                _logger?.Debug("[Playback] World-script success marker (Now Playing / Load Url) → cancelling silent-stall watchdog");
                CancelSilentStallWatchdog();
            }
        }
    }

    // Per-Opening watchdog. A new Opening supersedes any pending watchdog (cancels the old CTS,
    // installs a fresh one) so rapid retries / world script re-fires don't accumulate timers.
    private void StartSilentStallWatchdog(string url)
    {
        var newCts = new CancellationTokenSource();
        CancellationTokenSource? oldCts;
        lock (_silentStallLock)
        {
            oldCts = _silentStallCts;
            _silentStallCts = newCts;
            _silentStallUrl = url;
        }
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch { /* superseded — fine */ }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SilentStallWindow, newCts.Token);
            }
            catch (OperationCanceledException) { return; }

            string? activeUrl = null;
            lock (_silentStallLock)
            {
                if (ReferenceEquals(_silentStallCts, newCts))
                {
                    activeUrl = _silentStallUrl;
                    _silentStallCts = null;
                    _silentStallUrl = null;
                }
            }
            try { newCts.Dispose(); } catch { }
            if (activeUrl == null) return;
            try { OnAvProSilentStall?.Invoke(activeUrl, DateTime.UtcNow); }
            catch (Exception ex) { _logger?.Warning("[VrcLogMonitor] OnAvProSilentStall handler threw: " + ex.Message); }
        });
    }

    private void CancelSilentStallWatchdog()
    {
        CancellationTokenSource? cts;
        lock (_silentStallLock)
        {
            cts = _silentStallCts;
            _silentStallCts = null;
            _silentStallUrl = null;
        }
        try { cts?.Cancel(); cts?.Dispose(); } catch { /* fine */ }
    }

    private string? DetectToolsFromExe(string exePath)
    {
        try
        {
            string? root = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(root)) return null;
            string toolsDir = Path.Combine(root, "VRChat_Data", "StreamingAssets", "Tools");
            if (Directory.Exists(toolsDir)) return toolsDir;
        }
        catch (Exception ex) { _logger?.Debug("Failed to detect Tools dir from exe path: " + ex.Message); }
        return null;
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        try { _monitorTask?.Wait(1000); }
        catch { /* Shutdown cleanup — failure is expected */ }
        _cts.Dispose();
    }
}
