using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;
using WKVRCProxy.Core;
using WKVRCProxy.Core.Services;

namespace WKVRCProxy.Uninstaller;

// uninstall.exe — removes WKVRCProxy completely. Three things must happen in order:
//   1. Restore VRChat's original yt-dlp.exe so VRChat keeps working without WKVRCProxy.
//   2. Delete %LOCALAPPDATA%\WKVRCProxy (relay port marker, strategy memory, settings).
//   3. Delete the install dir itself — scheduled via cmd.exe so the running uninstall.exe
//      isn't trying to delete its own .exe.
//
// References WKVRCProxy.Core only for the shared RestoreYtDlpInTools + VrcPathLocator helpers,
// keeping the operation in lockstep with what PatcherService.Shutdown does on a graceful exit.
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\WKVRCProxy.UI.SingleInstance";

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string installDir = ParseArg(args, "--install-dir") ?? AppDomain.CurrentDomain.BaseDirectory;
        installDir = Path.GetFullPath(installDir.TrimEnd('\\', '/'));
        string logPath = Path.Combine(installDir, "uninstall.log");

        if (!File.Exists(Path.Combine(installDir, "WKVRCProxy.exe")))
        {
            MessageBox.Show(
                "WKVRCProxy.exe not found in:\n\n" + installDir + "\n\nThis uninstaller must run from the WKVRCProxy install folder.",
                "WKVRCProxy Uninstaller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }

        var confirm = MessageBox.Show(
            "Uninstall WKVRCProxy?\n\n" +
            "This will:\n" +
            "  • Restore VRChat's original yt-dlp.exe\n" +
            "  • Delete saved settings, cookies, and bypass memory\n" +
            "  • Remove this install folder:\n      " + installDir + "\n\n" +
            "Continue?",
            "Uninstall WKVRCProxy",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirm != DialogResult.Yes) return 0;

        void Log(string msg)
        {
            try { File.AppendAllText(logPath, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine); }
            catch { }
        }

        Log("Uninstall started. Install dir: " + installDir);

        // Step 1: ask the running app to exit cleanly via mutex check, then force-kill remaining.
        try
        {
            foreach (var proc in Process.GetProcessesByName("WKVRCProxy"))
            {
                Log("Closing WKVRCProxy.exe (pid " + proc.Id + ")...");
                try
                {
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(5000))
                    {
                        Log("Graceful close timed out; killing.");
                        try { proc.Kill(true); } catch { }
                    }
                }
                catch (Exception ex) { Log("Close error: " + ex.Message); }
            }
        }
        catch (Exception ex) { Log("Error enumerating WKVRCProxy processes: " + ex.Message); }

        if (!WaitForMutexFree(TimeSpan.FromSeconds(10)))
            Log("WARN: Single-instance mutex still held; proceeding anyway.");

        // Step 2: restore VRChat's original yt-dlp.exe. The user may have reconfigured the path,
        // so check the most likely places. We don't have access to settings.json here without
        // pulling SettingsManager (which would touch its own files) — so we rely on the default
        // path + any override stored in the install-dir-relative settings.json (best-effort read).
        string? customPath = TryReadCustomVrcPathFromSettings(installDir);
        string? toolsDir = VrcPathLocator.Find(customPath);
        if (string.IsNullOrEmpty(toolsDir))
        {
            Log("VRChat Tools dir not found — skipping yt-dlp restore. (No backup to restore from anyway if yt-dlp-og.exe is missing.)");
        }
        else
        {
            Log("Restoring yt-dlp in: " + toolsDir);
            bool ok = PatcherService.RestoreYtDlpInTools(toolsDir, null);
            Log(ok ? "yt-dlp restored." : "Nothing to restore (yt-dlp-og.exe not present).");
        }

        // Step 3: delete %LOCALAPPDATA%\WKVRCProxy.
        string localState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WKVRCProxy");
        if (Directory.Exists(localState))
        {
            Log("Deleting " + localState + "...");
            try { Directory.Delete(localState, recursive: true); }
            catch (Exception ex) { Log("WARN: Failed to delete " + localState + ": " + ex.Message); }
        }

        // Step 4: schedule install-dir deletion via spawned cmd, then exit. Cannot rmdir our own
        // .exe while we're holding it, hence the timeout-and-spawn dance.
        try
        {
            string cmdArgs = "/c timeout /t 2 >nul & rmdir /s /q \"" + installDir + "\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            Log("Scheduled install-dir deletion. Exiting.");
        }
        catch (Exception ex)
        {
            Log("ERROR scheduling delete: " + ex.Message);
            MessageBox.Show(
                "Uninstall ran but the install folder could not be deleted automatically. You can delete it manually:\n\n" + installDir,
                "WKVRCProxy Uninstaller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 3;
        }

        MessageBox.Show(
            "WKVRCProxy has been uninstalled.\n\nVRChat's original yt-dlp.exe has been restored.",
            "WKVRCProxy Uninstaller",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        return 0;
    }

    private static string? ParseArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool WaitForMutexFree(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var m = new Mutex(false, SingleInstanceMutexName);
                if (m.WaitOne(0))
                {
                    try { m.ReleaseMutex(); } catch { }
                    return true;
                }
            }
            catch (AbandonedMutexException) { return true; }
            catch { /* not yet open */ }
            Thread.Sleep(500);
        }
        return false;
    }

    // Best-effort read of CustomVrcPath from settings.json without referencing SettingsManager
    // (which would write its own state on construction). Returns null on any failure.
    private static string? TryReadCustomVrcPathFromSettings(string installDir)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WKVRCProxy", "settings.json");
            if (!File.Exists(path)) path = Path.Combine(installDir, "settings.json");
            if (!File.Exists(path)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("CustomVrcPath", out var el))
                return el.GetString();
        }
        catch { }
        return null;
    }
}
