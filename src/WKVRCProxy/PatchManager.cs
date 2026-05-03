using System.Runtime.Versioning;

namespace WKVRCProxy;

// Keeps VRChat's Tools\yt-dlp.exe pointed at the patched build that talks to our
// pipe, while preserving the vanilla original at yt-dlp-og.exe so the patched
// binary can fall back to it on resolve failure.
//
// Hardening over the legacy PatcherService:
//   - Refuses to start if there's nothing to preserve (Tools dir empty AND no
//     bundled fallback) — exits cleanly instead of looping.
//   - On every 3s tick, restores yt-dlp-og.exe from the bundled fallback if it
//     went missing, so the server-side fallback path stays functional.
//   - Halts (single line "Reinstall WKVRCProxy") if both the patched binary and
//     the bundled fallback are gone — should be unreachable.
[SupportedOSPlatform("windows")]
internal sealed class PatchManager : IDisposable
{
    private const int TickDelayMs = 3000;
    private const int MinReapplyGapSec = 3;

    private readonly string _patchedYtDlpPath;
    private readonly string _bundledFallbackPath;
    private readonly string _bundledFallbackVerPath;
    private readonly string _cleanExitFlagPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _vrcToolsDir;
    private Task? _loop;
    private DateTime _lastPatchTime = DateTime.MinValue;
    private bool _halted;

    public string? VrcToolsDir => _vrcToolsDir;
    public bool Halted => _halted;

    public PatchManager(string installDir)
    {
        _patchedYtDlpPath = Path.Combine(installDir, "tools", "yt-dlp-patched.exe");
        _bundledFallbackPath = Path.Combine(installDir, "tools", "yt-dlp-og-fallback.exe");
        _bundledFallbackVerPath = Path.Combine(installDir, "tools", "yt-dlp-og-fallback.version.txt");

        string stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WKVRCProxy");
        Directory.CreateDirectory(stateDir);
        _cleanExitFlagPath = Path.Combine(stateDir, "clean_exit.flag");

        _vrcToolsDir = VrcPathLocator.Find();
    }

    // Run once at startup before Start(). If the previous shutdown was unclean,
    // try to put the Tools folder back into a sane state so the watchdog can
    // engage from a known baseline.
    public void RecoverFromUncleanShutdown()
    {
        bool cleanLastTime = File.Exists(_cleanExitFlagPath);
        if (cleanLastTime)
        {
            try { File.Delete(_cleanExitFlagPath); } catch { /* best-effort */ }
            return;
        }

        if (string.IsNullOrEmpty(_vrcToolsDir) || !Directory.Exists(_vrcToolsDir)) return;

        string targetPath = Path.Combine(_vrcToolsDir, "yt-dlp.exe");
        string backupPath = Path.Combine(_vrcToolsDir, "yt-dlp-og.exe");

        if (File.Exists(backupPath))
        {
            Console.WriteLine("Recovery: previous run exited uncleanly — restoring vanilla yt-dlp from yt-dlp-og.exe.");
            RestoreYtDlpInTools(_vrcToolsDir);
            return;
        }

        if (File.Exists(targetPath) && File.Exists(_patchedYtDlpPath) && FilesEqualByHash(targetPath, _patchedYtDlpPath))
        {
            try
            {
                File.Delete(targetPath);
                Console.WriteLine("Recovery: orphan patched yt-dlp.exe deleted; vanilla copy will be re-staged.");
            }
            catch { /* next tick will retry */ }
        }
    }

    public bool Start()
    {
        if (string.IsNullOrEmpty(_vrcToolsDir))
        {
            Console.WriteLine("Cannot apply patch — VRChat Tools folder not found. Launch VRChat once first, then re-run.");
            return false;
        }
        if (!File.Exists(_patchedYtDlpPath))
        {
            Console.WriteLine("Cannot apply patch — patched yt-dlp.exe is missing from this install. Reinstall WKVRCProxy.");
            return false;
        }

        string targetPath = Path.Combine(_vrcToolsDir, "yt-dlp.exe");
        string backupPath = Path.Combine(_vrcToolsDir, "yt-dlp-og.exe");
        if (!File.Exists(targetPath) && !File.Exists(backupPath))
        {
            Console.WriteLine("Cannot apply patch — VRChat hasn't shipped its own yt-dlp.exe yet, and we have no original to preserve as fallback. Launch VRChat once first, then re-run.");
            return false;
        }

        _loop = Task.Run(WatchdogLoop);
        return true;
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (!string.IsNullOrEmpty(_vrcToolsDir))
            RestoreYtDlpInTools(_vrcToolsDir);

        try { File.WriteAllText(_cleanExitFlagPath, DateTime.UtcNow.ToString("o")); } catch { /* best-effort */ }
    }

    private async Task WatchdogLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { TickOnce(); }
            catch (Exception ex) { Console.WriteLine("[patch] tick error: " + ex.Message); }
            try { await Task.Delay(TickDelayMs, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void TickOnce()
    {
        if (_halted) return;
        if (string.IsNullOrEmpty(_vrcToolsDir)) return;

        string targetPath = Path.Combine(_vrcToolsDir, "yt-dlp.exe");
        string backupPath = Path.Combine(_vrcToolsDir, "yt-dlp-og.exe");

        if (!File.Exists(backupPath))
        {
            if (File.Exists(targetPath) && !FilesEqualByHash(targetPath, _patchedYtDlpPath))
            {
                File.Move(targetPath, backupPath);
            }
            else if (File.Exists(_bundledFallbackPath))
            {
                File.Copy(_bundledFallbackPath, backupPath, true);
                Console.WriteLine("yt-dlp-og.exe was missing — restored from bundled fallback (" + ReadBundledFallbackVersion() + "). Server-side fallback path is now functional again.");
            }
            else
            {
                _halted = true;
                Console.WriteLine("Reinstall WKVRCProxy");
                _cts.Cancel();
                return;
            }
        }

        if (!File.Exists(_patchedYtDlpPath))
        {
            _halted = true;
            Console.WriteLine("Reinstall WKVRCProxy");
            _cts.Cancel();
            return;
        }

        if (!File.Exists(targetPath))
        {
            File.Copy(_patchedYtDlpPath, targetPath, true);
            _lastPatchTime = DateTime.UtcNow;
            return;
        }

        if (FilesEqualByHash(targetPath, _patchedYtDlpPath)) return;
        if ((DateTime.UtcNow - _lastPatchTime).TotalSeconds < MinReapplyGapSec) return;

        try
        {
            File.Copy(_patchedYtDlpPath, targetPath, true);
            _lastPatchTime = DateTime.UtcNow;
            Console.WriteLine("[patch] yt-dlp.exe was overwritten — re-applied.");
        }
        catch (IOException) { /* file in use — retry next tick */ }
    }

    public static bool RestoreYtDlpInTools(string toolsDir)
    {
        if (string.IsNullOrEmpty(toolsDir) || !Directory.Exists(toolsDir)) return false;
        string targetPath = Path.Combine(toolsDir, "yt-dlp.exe");
        string backupPath = Path.Combine(toolsDir, "yt-dlp-og.exe");
        if (!File.Exists(backupPath)) return false;

        try
        {
            if (File.Exists(targetPath))
            {
                try { File.Delete(targetPath); }
                catch (IOException)
                {
                    string stale = targetPath + ".stale-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    File.Move(targetPath, stale);
                    Console.WriteLine("[patch] yt-dlp.exe was locked; moved aside to " + Path.GetFileName(stale) + ".");
                }
            }
            File.Move(backupPath, targetPath);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[patch] restore error: " + ex.Message);
            return false;
        }
    }

    private static bool FilesEqualByHash(string a, string b)
    {
        string ha = HashUtils.GetFileHash(a);
        string hb = HashUtils.GetFileHash(b);
        if (string.IsNullOrEmpty(ha) || string.IsNullOrEmpty(hb)) return false;
        return ha == hb;
    }

    private string ReadBundledFallbackVersion()
    {
        try
        {
            if (File.Exists(_bundledFallbackVerPath))
                return File.ReadAllText(_bundledFallbackVerPath).Trim();
        }
        catch { /* best-effort */ }
        return "unknown";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
