using System.Runtime.Versioning;
using WKVRCProxy.Shared;

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
    private readonly string _haltFlagPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _vrcToolsDir;
    private Task? _loop;
    private DateTime _lastPatchTime = DateTime.MinValue;
    private bool _halted;
    private bool _started;

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
        _haltFlagPath = Path.Combine(stateDir, "halt.flag");

        _vrcToolsDir = VrcPathLocator.Find();
    }

    // Run once at startup before Start(). If the previous shutdown was unclean,
    // try to put the Tools folder back into a sane state so the watchdog can
    // engage from a known baseline.
    public void RecoverFromUncleanShutdown()
    {
        // Always sweep our sidecars first, regardless of clean/unclean flag.
        // .new-<short> tmps from a kill-mid-AtomicCopy and .stale-<utc>
        // rename-asides accumulate across runs otherwise.
        ToolsDirSweeper.Sweep(_vrcToolsDir);

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

        // We've confirmed both the patched binary and a Tools-side state we can
        // engage with — clear any leftover halt.flag from a prior corrupted run.
        try { if (File.Exists(_haltFlagPath)) File.Delete(_haltFlagPath); }
        catch { /* best-effort */ }

        _started = true;
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

        // Only write clean_exit.flag when the post-shutdown state is genuinely
        // clean. If we ever engaged the watchdog, the restore must have actually
        // succeeded for the flag to be honest. If it didn't (og missing, target
        // locked through every retry, etc.) we leave the flag absent so the
        // next launch's RecoverFromUncleanShutdown gets a chance to fix it.
        bool cleanShutdown;
        if (!_started)
        {
            // Watchdog never engaged — Tools dir was untouched by us this run.
            cleanShutdown = true;
        }
        else if (string.IsNullOrEmpty(_vrcToolsDir))
        {
            cleanShutdown = true;
        }
        else
        {
            cleanShutdown = RestoreYtDlpInTools(_vrcToolsDir);
            // Sweep our own sidecars whether the restore succeeded or not.
            // The .stale-<utc> file produced by the locked-target branch of
            // RestoreYtDlpInTools is exactly the kind of leftover this is
            // here to clean up.
            ToolsDirSweeper.Sweep(_vrcToolsDir);
        }

        if (cleanShutdown)
        {
            try { File.WriteAllText(_cleanExitFlagPath, DateTime.UtcNow.ToString("o")); }
            catch { /* best-effort */ }
        }
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
                try { File.Move(targetPath, backupPath); }
                catch (IOException) { return; } // target locked — retry next tick
            }
            else if (File.Exists(_bundledFallbackPath))
            {
                try
                {
                    AtomicCopy(_bundledFallbackPath, backupPath);
                    Console.WriteLine("yt-dlp-og.exe was missing — restored from bundled fallback (" + ReadBundledFallbackVersion() + "). Server-side fallback path is now functional again.");
                }
                catch (IOException) { return; } // disk-full / locked — retry next tick
            }
            else
            {
                // No backup AND no bundled fallback AND target == patched. We have
                // nothing to roll back to — VRChat will be stuck with the patched
                // binary even after we shut down. The patched yt-dlp's own
                // "exec yt-dlp-og.exe" fallback can't fire because og.exe is gone.
                // RestoreYtDlpInTools will return false (no backup); the halt
                // banner stays visible regardless.
                Halt("install_corrupted_no_backup_no_fallback");
                return;
            }
        }

        if (!File.Exists(_patchedYtDlpPath))
        {
            // Our own patched build is missing from the install. og.exe should
            // still be present (otherwise the previous branch would have halted
            // first). Restore from og so VRChat is left with vanilla yt-dlp.
            Halt("patched_binary_missing");
            return;
        }

        if (!File.Exists(targetPath))
        {
            try
            {
                AtomicCopy(_patchedYtDlpPath, targetPath);
                _lastPatchTime = DateTime.UtcNow;
            }
            catch (IOException) { /* will retry next tick */ }
            return;
        }

        if (FilesEqualByHash(targetPath, _patchedYtDlpPath)) return;
        if ((DateTime.UtcNow - _lastPatchTime).TotalSeconds < MinReapplyGapSec) return;

        try
        {
            AtomicCopy(_patchedYtDlpPath, targetPath);
            _lastPatchTime = DateTime.UtcNow;
            Console.WriteLine("[patch] yt-dlp.exe was overwritten — re-applied.");
        }
        catch (IOException) { /* file in use — retry next tick */ }
    }

    // Halt the watchdog loop. ALWAYS attempts to leave VRChat with a working
    // yt-dlp.exe before stopping ticks — restoring from yt-dlp-og.exe if it
    // still exists. On a successful restore VRChat falls back to vanilla
    // yt-dlp behaviour the next time it launches a video. Persists a halt.flag
    // (with reason + timestamp) so a future maintenance pass can detect that
    // the daemon is alive but no longer functional.
    private void Halt(string reason)
    {
        _halted = true;
        bool restored = false;
        if (!string.IsNullOrEmpty(_vrcToolsDir))
        {
            try { restored = RestoreYtDlpInTools(_vrcToolsDir); }
            catch (Exception ex) { Console.WriteLine("[halt] restore threw: " + ex.Message); }
            ToolsDirSweeper.Sweep(_vrcToolsDir);
        }
        Console.WriteLine("Reinstall WKVRCProxy");
        Console.WriteLine("[halt] reason=" + reason + " restored=" + restored);
        try { File.WriteAllText(_haltFlagPath, DateTime.UtcNow.ToString("o") + " " + reason); }
        catch { /* best-effort */ }
        _cts.Cancel();
    }

    // Copies src over dst with no partial-file window: stage to a sibling
    // tmp on dst's volume, then atomic same-volume rename. A kill mid-copy
    // leaves dst untouched (still vanilla or still patched, but never
    // truncated). The tmp file is cleaned up on any failure path.
    private static void AtomicCopy(string src, string dst)
    {
        string tmp = dst + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            File.Copy(src, tmp, overwrite: true);
            File.Move(tmp, dst, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    public static bool RestoreYtDlpInTools(string toolsDir)
    {
        if (string.IsNullOrEmpty(toolsDir) || !Directory.Exists(toolsDir)) return false;
        string targetPath = Path.Combine(toolsDir, "yt-dlp.exe");
        string backupPath = Path.Combine(toolsDir, "yt-dlp-og.exe");
        if (!File.Exists(backupPath)) return false;

        try
        {
            // Fast path: atomic same-volume rename. No window where target is missing.
            try
            {
                File.Move(backupPath, targetPath, overwrite: true);
                return true;
            }
            catch (IOException)
            {
                // Target is locked (VRChat / AV holding it open). Move it aside, then retry.
            }

            if (File.Exists(targetPath))
            {
                string stale = targetPath + ".stale-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                File.Move(targetPath, stale);
                Console.WriteLine("[patch] yt-dlp.exe was locked; moved aside to " + Path.GetFileName(stale) + ".");
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
