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

    // Correlation state for the Opening → Error pattern. We only keep the most recent Opening URL
    // and its observation time; a "Loading failed" within CorrelationWindow demotes. Older Openings
    // are cleared on state transitions.
    private string? _lastAvProOpeningUrl;
    private DateTime _lastAvProOpeningAt;
    private static readonly TimeSpan AvProOpenFailCorrelationWindow = TimeSpan.FromSeconds(10);
    private static readonly Regex _avProOpeningRegex = new(
        @"\[AVProVideo\] Opening\s+(\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                _lastAvProOpeningUrl = openingMatch.Groups[1].Value;
                _lastAvProOpeningAt = DateTime.UtcNow;
            }
            else if (line.Contains("[AVProVideo] Error: Loading failed"))
            {
                if (_lastAvProOpeningUrl != null
                    && DateTime.UtcNow - _lastAvProOpeningAt <= AvProOpenFailCorrelationWindow)
                {
                    string failedUrl = _lastAvProOpeningUrl;
                    _lastAvProOpeningUrl = null; // consume to avoid double-firing on retry errors
                    try { OnAvProLoadFailure?.Invoke(failedUrl, DateTime.UtcNow); }
                    catch (Exception ex) { _logger?.Warning("[VrcLogMonitor] OnAvProLoadFailure handler threw: " + ex.Message); }
                }
            }
        }
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
