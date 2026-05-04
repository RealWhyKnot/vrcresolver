using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Watches VRChat's output_log_*.txt for AVPro playback events. The mesh
// dispatcher can't see whether VRChat's player actually played a URL it
// returned — only the watchdog has that visibility (it tails the same
// log VRChat writes). VrcLogMonitor restores the legacy feedback path:
// observe failures + stalls, forward as `playback_feedback` mesh frames
// so the server-side strategy quality scoring can demote whichever
// strategy/config produced a URL AVPro couldn't actually load.
//
// Two failure shapes:
//   load_failure — `[AVProVideo] Opening <url>` followed by
//                  `[AVProVideo] Error: Loading failed` within 10 s.
//                  This is the loud failure (AVPro tried, surfaced an
//                  error). Correlated by URL + recency.
//   silent_stall — Opening line followed by 12 s of NOTHING
//                  (no error, no `Using playback path:` success marker,
//                  no world-script `Now Playing:` / `Load Url:` line).
//                  Captures worlds that don't surface their failures via
//                  AVPro (e.g. a world that catches the exception in its
//                  Udon graph and drops it on the floor).
//
// Cancellations: a new Opening supersedes any pending stall watchdog.
// A `Loading failed` (whether correlated or not) cancels the stall
// watchdog so we don't double-report. A success marker cancels too.
[SupportedOSPlatform("windows")]
internal sealed class VrcLogMonitor : IDisposable
{
    private static readonly TimeSpan AvProOpenFailCorrelationWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SilentStallWindow = TimeSpan.FromSeconds(12);
    private static readonly Regex AvProOpeningRegex = new(
        @"\[AVProVideo\] Opening\s+(\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly MeshClient _mesh;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    // Most recent Opening line we've seen. The Loading-failed line that
    // arrives within the correlation window is matched to this; older
    // Openings are cleared when consumed or superseded.
    private string? _lastOpeningUrl;
    private DateTime _lastOpeningAt;

    private CancellationTokenSource? _stallCts;
    private string? _stallUrl;
    private DateTime _stallAt;
    private readonly object _stallLock = new();

    public VrcLogMonitor(MeshClient mesh) { _mesh = mesh; }

    public void Start()
    {
        _loop = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        CancelStallWatchdog();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        string vrcDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
            "VRChat", "VRChat");
        string currentFile = "";
        long lastSize = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Directory.Exists(vrcDir))
                {
                    // VRChat not installed yet, or installed somewhere
                    // non-default. Re-poll periodically — VrcPathLocator
                    // already handles the path resolution side; we just
                    // wait for the dir to materialize.
                    try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch { return; }
                    continue;
                }

                FileInfo? latest = null;
                try
                {
                    latest = new DirectoryInfo(vrcDir)
                        .GetFiles("output_log*.txt")
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();
                }
                catch { /* dir disappeared mid-enumeration; retry */ }

                if (latest != null)
                {
                    if (latest.FullName != currentFile)
                    {
                        currentFile = latest.FullName;
                        lastSize = 0;
                    }
                    if (latest.Length > lastSize)
                    {
                        try
                        {
                            using var fs = new FileStream(currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            fs.Seek(lastSize, SeekOrigin.Begin);
                            using var reader = new StreamReader(fs);
                            string newContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                            lastSize = fs.Position;
                            ProcessNewContent(newContent);
                        }
                        catch (IOException) { /* file rotated / locked; retry next tick */ }
                    }
                    else if (latest.Length < lastSize)
                    {
                        // Log file rotated mid-run — VRChat truncated or
                        // replaced the file. Reset offset and re-read from
                        // the new start on next tick.
                        lastSize = 0;
                    }
                }

                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { return; }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Console.WriteLine("[vrclog] monitor error: " + ex.GetType().Name + ": " + ex.Message);
                try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    private void ProcessNewContent(string content)
    {
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var openingMatch = AvProOpeningRegex.Match(line);
            if (openingMatch.Success)
            {
                string opened = openingMatch.Groups[1].Value;
                _lastOpeningUrl = opened;
                _lastOpeningAt = DateTime.UtcNow;
                StartStallWatchdog(opened);
                continue;
            }

            if (line.Contains("[AVProVideo] Error: Loading failed"))
            {
                if (_lastOpeningUrl != null
                    && DateTime.UtcNow - _lastOpeningAt <= AvProOpenFailCorrelationWindow)
                {
                    string failed = _lastOpeningUrl;
                    int ms = (int)(DateTime.UtcNow - _lastOpeningAt).TotalMilliseconds;
                    _lastOpeningUrl = null; // consume
                    _ = _mesh.SendPlaybackFeedbackAsync(failed, WireConstants.PlaybackFeedbackLoadFailure, ms);
                    Console.WriteLine("[vrclog] load_failure ms=" + ms + " url=" + LogUtil.SanitizeForConsole(failed, 96));
                }
                CancelStallWatchdog();
                continue;
            }

            // Success markers — any one of these proves AVPro accepted
            // the URL and started loading it; the stall watchdog should
            // be cancelled to avoid a false-positive silent_stall report.
            if (line.Contains("[AVProVideo] Using playback path:")
                || line.Contains("Now Playing:")
                || line.Contains("Load Url:"))
            {
                CancelStallWatchdog();
            }
        }
    }

    private void StartStallWatchdog(string url)
    {
        var newCts = new CancellationTokenSource();
        CancellationTokenSource? oldCts;
        DateTime now = DateTime.UtcNow;
        lock (_stallLock)
        {
            oldCts = _stallCts;
            _stallCts = newCts;
            _stallUrl = url;
            _stallAt = now;
        }
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch { /* superseded */ }

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SilentStallWindow, newCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            string? activeUrl;
            DateTime activeAt;
            lock (_stallLock)
            {
                if (!ReferenceEquals(_stallCts, newCts)) return;
                activeUrl = _stallUrl;
                activeAt = _stallAt;
                _stallCts = null;
                _stallUrl = null;
            }
            try { newCts.Dispose(); } catch { /* ignore */ }
            if (activeUrl == null) return;

            int ms = (int)(DateTime.UtcNow - activeAt).TotalMilliseconds;
            _ = _mesh.SendPlaybackFeedbackAsync(activeUrl, WireConstants.PlaybackFeedbackSilentStall, ms);
            Console.WriteLine("[vrclog] silent_stall ms=" + ms + " url=" + LogUtil.SanitizeForConsole(activeUrl, 96));
        });
    }

    private void CancelStallWatchdog()
    {
        CancellationTokenSource? cts;
        lock (_stallLock)
        {
            cts = _stallCts;
            _stallCts = null;
            _stallUrl = null;
        }
        try { cts?.Cancel(); cts?.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        CancelStallWatchdog();
        _cts.Dispose();
    }
}
