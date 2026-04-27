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

        // Internal re-entry path: when we need admin to write the hosts file, the regular
        // uninstaller spawns a copy of itself with --remove-hosts-entry and Verb=runas. The
        // elevated copy hits this branch, edits hosts, exits. Same pattern HostsManager uses for
        // setup so the rest of the uninstall flow can stay non-elevated.
        if (HasFlag(args, "--remove-hosts-entry"))
        {
            return RemoveHostsEntryElevated();
        }

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

        bool hostsEntryPresent = IsHostsEntryPresent();
        string hostsLine = hostsEntryPresent
            ? "  • Remove the localhost.youtube.com line from your hosts file (one UAC prompt)\n"
            : "";

        var confirm = MessageBox.Show(
            "Uninstall WKVRCProxy?\n\n" +
            "This will:\n" +
            "  • Restore VRChat's original yt-dlp.exe\n" +
            hostsLine +
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

        // Step 2.5: remove the hosts file entry (`127.0.0.1 localhost.youtube.com`) if it's there.
        // Spawn self elevated since System32\drivers\etc\hosts is admin-only. UAC declined → leave
        // it alone with a warning rather than block the rest of uninstall.
        if (hostsEntryPresent)
        {
            Log("Hosts entry present; spawning elevated removal...");
            try
            {
                var hostsProc = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule?.FileName ?? "",
                    Arguments = "--remove-hosts-entry",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var p = Process.Start(hostsProc);
                p?.WaitForExit(15000);
                if (p == null || p.ExitCode != 0)
                {
                    Log("WARN: Elevated hosts removal exited with code " + (p?.ExitCode.ToString() ?? "(null)") + ".");
                }
                else if (IsHostsEntryPresent())
                {
                    Log("WARN: Hosts entry still present after elevated run.");
                }
                else
                {
                    Log("Hosts entry removed.");
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Log("User canceled UAC for hosts removal. Leaving the entry in place.");
            }
            catch (Exception ex)
            {
                Log("ERROR spawning elevated hosts removal: " + ex.Message);
            }
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

    private static bool HasFlag(string[] args, string name)
    {
        foreach (var a in args)
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string HostsFilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    // Mirrors HostsManager.IsBypassActive — duplicated so the uninstaller stays free of a UI
    // dependency. Match shape: ignore comments, look for any line containing both 127.0.0.1
    // and localhost.youtube.com.
    private static bool IsHostsEntryPresent()
    {
        try
        {
            string path = HostsFilePath();
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var rd = new StreamReader(fs);
            string? raw;
            while ((raw = rd.ReadLine()) != null)
            {
                string line = raw.Trim();
                if (line.StartsWith("#")) continue;
                if (line.Contains("127.0.0.1") && line.Contains("localhost.youtube.com")) return true;
            }
        }
        catch { /* unreadable hosts file → treat as no entry; not worth blocking uninstall */ }
        return false;
    }

    // Elevated path. Reads hosts, drops any line that contains both 127.0.0.1 and
    // localhost.youtube.com, writes back. Preserves line endings as best we can — hosts is CRLF
    // on Windows by convention but operators sometimes hand-edit with LF.
    private static int RemoveHostsEntryElevated()
    {
        try
        {
            string path = HostsFilePath();
            if (!File.Exists(path)) return 0;
            string content = File.ReadAllText(path);
            string newline = content.Contains("\r\n") ? "\r\n" : "\n";
            var kept = new System.Text.StringBuilder();
            bool removedAny = false;
            foreach (var raw in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string trimmed = raw.Trim();
                if (!trimmed.StartsWith("#")
                    && trimmed.Contains("127.0.0.1")
                    && trimmed.Contains("localhost.youtube.com"))
                {
                    removedAny = true;
                    continue;
                }
                kept.Append(raw).Append(newline);
            }
            if (!removedAny) return 0;
            // Trim one trailing newline so we don't accumulate blank lines on each
            // setup/uninstall cycle.
            string output = kept.ToString();
            if (output.EndsWith(newline + newline))
                output = output.Substring(0, output.Length - newline.Length);
            File.WriteAllText(path, output);
            return 0;
        }
        catch (Exception ex)
        {
            // No log path here — this is the elevated child, started without --install-dir. The
            // parent waits on exit code; non-zero surfaces the failure.
            try
            {
                MessageBox.Show(
                    "Could not edit the hosts file:\n\n" + ex.Message + "\n\n" +
                    "You can remove the line `127.0.0.1 localhost.youtube.com` manually from\n" +
                    HostsFilePath(),
                    "WKVRCProxy Uninstaller",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch { }
            return 1;
        }
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
