using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

[SupportedOSPlatform("windows")]
public class PatcherService : IProxyModule, IDisposable
{
    public string Name => "Patcher";
    public string? VrcToolsDir => _vrcToolsDir;

    private Logger? _logger;
    private IModuleContext? _context;
    private string? _vrcToolsDir;
    private string? _wrapperPath;
    private bool _isPatchDesired = false;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private DateTime _lastPatchTime = DateTime.MinValue;

    public Task InitializeAsync(IModuleContext context)
    {
        _context = context;
        _logger = context.Logger;
        DetectVrcPath();
        return Task.CompletedTask;
    }

    private void DetectVrcPath()
    {
        try
        {
            string? custom = _context?.Settings.Config.CustomVrcPath;
            if (!string.IsNullOrEmpty(custom) && !Directory.Exists(custom))
                _logger?.Warning("Custom VRChat path configured but does not exist: " + custom);

            _vrcToolsDir = VrcPathLocator.Find(custom);
            if (!string.IsNullOrEmpty(_vrcToolsDir))
                _logger?.Success((string.IsNullOrEmpty(custom) ? "VRChat Tools found: " : "Using Custom VRChat Tools path: ") + _vrcToolsDir);
            else
                _logger?.Warning("VRChat Tools folder missing at default location.");
        }
        catch (Exception ex) { _logger?.Error("Path Detection Error: " + ex.Message); }
    }

    public void UpdateToolsDir(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        if (_vrcToolsDir == path) return;

        _vrcToolsDir = path;
        _logger?.Success("Tools path updated: " + _vrcToolsDir);
    }

    public void WipeToolsFolder()
    {
        if (string.IsNullOrEmpty(_vrcToolsDir) || !Directory.Exists(_vrcToolsDir)) return;

        _logger?.Info("Wiping VRChat Tools folder for clean state...");
        try
        {
            Shutdown();
            foreach (var f in Directory.GetFiles(_vrcToolsDir))
            {
                try { File.Delete(f); }
                catch (Exception ex) { _logger?.Debug("Failed to delete file during wipe (" + Path.GetFileName(f) + "): " + ex.Message); }
            }
            foreach (var d in Directory.GetDirectories(_vrcToolsDir))
            {
                try { Directory.Delete(d, true); }
                catch (Exception ex) { _logger?.Debug("Failed to delete directory during wipe (" + Path.GetFileName(d) + "): " + ex.Message); }
            }
            _logger?.Success("Tools folder wiped.");
        }
        catch (Exception ex) { _logger?.Error("Wipe failed: " + ex.Message); }
    }

    public void StartMonitoring(string wrapperPath)
    {
        _wrapperPath = wrapperPath;
        _isPatchDesired = true;
        if (_monitorTask == null) _monitorTask = Task.Run(MonitorLoop);
    }

    public List<string> GetJunkItems()
    {
        var junk = new List<string>();
        if (string.IsNullOrEmpty(_vrcToolsDir) || !Directory.Exists(_vrcToolsDir)) return junk;

        try
        {
            foreach (var f in Directory.GetFiles(_vrcToolsDir))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("yt-dlp.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("yt-dlp-og.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("relay_port.dat", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("ipc_port.dat", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("yt-dlp-wrapper.log", StringComparison.OrdinalIgnoreCase)) continue;
                junk.Add(f);
            }
            foreach (var d in Directory.GetDirectories(_vrcToolsDir))
            {
                junk.Add(d);
            }
        }
        catch (Exception ex) { _logger?.Warning("Failed to enumerate junk items in Tools folder: " + ex.Message); }
        return junk;
    }

    public void CleanupJunk()
    {
        var items = GetJunkItems();
        if (items.Count == 0) return;

        _logger?.Info("Cleaning up " + items.Count + " junk items from Tools folder...");
        foreach (var item in items)
        {
            try
            {
                if (File.Exists(item)) File.Delete(item);
                else if (Directory.Exists(item)) Directory.Delete(item, true);
            }
            catch (Exception ex) { _logger?.Debug("Cleanup failed for " + item + ": " + ex.Message); }
        }
        _logger?.Success("Tools folder cleaned.");
    }

    private async Task MonitorLoop()
    {
        _logger?.Info("Patch monitor active.");
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_isPatchDesired && !string.IsNullOrEmpty(_vrcToolsDir) && !string.IsNullOrEmpty(_wrapperPath))
                {
                    await EnsurePatchApplied();
                }
            }
            catch (Exception ex) { _logger?.Warning("Patch monitor error: " + ex.Message, ex); }
            await Task.Delay(3000, _cts.Token);
        }
    }

    private async Task EnsurePatchApplied()
    {
        string targetPath = Path.Combine(_vrcToolsDir!, "yt-dlp.exe");
        string backupPath = Path.Combine(_vrcToolsDir!, "yt-dlp-og.exe");

        if (!File.Exists(targetPath)) 
        {
            try {
                File.Copy(_wrapperPath!, targetPath, true);
                _lastPatchTime = DateTime.Now;
                _logger?.Success("Patch applied (yt-dlp.exe created).");
            } catch (Exception ex) { _logger?.Error("Failed to apply patch (copy yt-dlp.exe): " + ex.Message, ex); }
            return;
        }

        if (!File.Exists(backupPath))
        {
            try {
                File.Move(targetPath, backupPath);
                File.Copy(_wrapperPath!, targetPath, true);
                _lastPatchTime = DateTime.Now;
                _logger?.Info("Patch initialized (Backup created).");
            } catch (Exception ex) { _logger?.Error("Failed to initialize patch (backup/copy): " + ex.Message, ex); }
            return;
        }

        if (IsFileSame(targetPath, _wrapperPath!)) return;
        if ((DateTime.Now - _lastPatchTime).TotalSeconds < 3) return;

        try
        {
            File.Copy(_wrapperPath!, targetPath, true);
            _lastPatchTime = DateTime.Now;
            _logger?.Warning("Patch integrity restored (yt-dlp.exe was modified or replaced).");
        }
        catch (Exception ex) { _logger?.Warning("Failed to restore patch integrity (file in use, will retry): " + ex.Message); }
    }

    private bool IsFileSame(string path1, string path2)
    {
        string hash1 = HashUtils.GetFileHash(path1);
        string hash2 = HashUtils.GetFileHash(path2);
        if (string.IsNullOrEmpty(hash1) || string.IsNullOrEmpty(hash2)) return false;
        return hash1 == hash2;
    }

    public ModuleHealthReport GetHealthReport()
    {
        bool hasToolsDir = !string.IsNullOrEmpty(_vrcToolsDir) && Directory.Exists(_vrcToolsDir);
        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = hasToolsDir ? HealthStatus.Healthy : HealthStatus.Degraded,
            Reason = hasToolsDir ? "" : "VRChat Tools folder not found",
            LastChecked = DateTime.Now
        };
    }

    public void Shutdown()
    {
        _isPatchDesired = false;
        _cts.Cancel();

        // Restore yt-dlp.exe — only possible if we know the tools dir.
        if (!string.IsNullOrEmpty(_vrcToolsDir))
            RestoreYtDlpInTools(_vrcToolsDir, _logger);

        // Process cleanup — always runs regardless of tools dir or restore outcome.
        // ProcessGuard (Job Object) handles the primary kill; these are belt-and-suspenders
        // fallbacks for any stray processes that were started outside the job (e.g. after a
        // hard parent crash where the job object never closed cleanly).
        KillStrayChildren(new[]
        {
            "curl-impersonate-win",
            "bgutil-ytdlp-pot-provider",
            "wireproxy",
            "wgcf",
            "streamlink",
        });

        string relayPortFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy", "relay_port.dat");
        if (File.Exists(relayPortFile))
        {
            try { File.Delete(relayPortFile); }
            catch { /* Shutdown cleanup — failure is expected */ }
        }
    }

    private static void KillStrayChildren(string[] processNames)
    {
        foreach (var name in processNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try { proc.Kill(); }
                catch { /* Shutdown cleanup — failure is expected */ }
            }
        }
    }

    // Restores VRChat's original yt-dlp.exe by promoting yt-dlp-og.exe back over the patched copy.
    // Public + static so the standalone uninstall.exe can invoke it without a module context, and
    // so PatcherService.Shutdown() and the recovery path share a single implementation. If the
    // patched yt-dlp.exe is locked at delete-time (rare — usually means VRChat is mid-launch) the
    // redirector is renamed to yt-dlp.exe.stale-<ts> so the restore can still proceed; the next
    // launch's CleanupJunk() removes the stale file.
    public static bool RestoreYtDlpInTools(string toolsDir, Logger? log)
    {
        if (string.IsNullOrEmpty(toolsDir) || !Directory.Exists(toolsDir)) return false;
        string targetPath = Path.Combine(toolsDir, "yt-dlp.exe");
        string backupPath = Path.Combine(toolsDir, "yt-dlp-og.exe");

        if (!File.Exists(backupPath)) return false;

        try
        {
            log?.Info("Restoring original yt-dlp.exe...");
            if (File.Exists(targetPath))
            {
                try { File.Delete(targetPath); }
                catch (IOException)
                {
                    string stale = targetPath + ".stale-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    File.Move(targetPath, stale);
                    log?.Warning("yt-dlp.exe was locked; moved aside to " + Path.GetFileName(stale) + " so restore could proceed.");
                }
            }
            File.Move(backupPath, targetPath);
            log?.Success("Original state restored.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Error("Restore Error: " + ex.Message);
            return false;
        }
    }

    // Auto-recover from a previous unclean exit. Run once at startup before patching resumes.
    // Three states we can encounter in the VRChat Tools dir:
    //   1. yt-dlp-og.exe present  → patch was applied last run and never reverted; restore.
    //   2. only yt-dlp.exe and it hashes equal to the bundled redirector → orphaned shim, delete
    //      so the next monitor pass copies the real yt-dlp back from vendor.
    //   3. otherwise → user's real yt-dlp; leave alone.
    public void RecoverFromUncleanShutdown(string redirectorPath)
    {
        if (string.IsNullOrEmpty(_vrcToolsDir) || !Directory.Exists(_vrcToolsDir)) return;

        string backupPath = Path.Combine(_vrcToolsDir, "yt-dlp-og.exe");
        string targetPath = Path.Combine(_vrcToolsDir, "yt-dlp.exe");

        if (File.Exists(backupPath))
        {
            _logger?.Warning("Detected unclean previous shutdown — restoring yt-dlp from yt-dlp-og.exe before re-patching.");
            RestoreYtDlpInTools(_vrcToolsDir, _logger);
            return;
        }

        if (File.Exists(targetPath) && File.Exists(redirectorPath) && IsFileSame(targetPath, redirectorPath))
        {
            try
            {
                File.Delete(targetPath);
                _logger?.Warning("Detected orphaned redirector at yt-dlp.exe with no backup — deleted; vendor copy will be re-staged.");
            }
            catch (Exception ex) { _logger?.Warning("Failed to delete orphaned redirector: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        Shutdown();
        _cts.Dispose();
    }
}
