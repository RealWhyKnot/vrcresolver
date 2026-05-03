using System.Diagnostics;
using System.Runtime.Versioning;

namespace WKVRCProxy.Uninstaller;

// No flags. No prompt. Running this exe IS consent.
//
// 1. Close any running WKVRCProxy.exe
// 2. Restore yt-dlp.exe from yt-dlp-og.exe in VRChat Tools (belt-and-suspenders:
//    drop the bundled vanilla in if og went missing)
// 3. Remove the hosts entry (UAC re-exec via WKVRCProxy.exe --remove-hosts-entry)
// 4. Wipe %LOCALAPPDATA%\WKVRCProxy\
// 5. Schedule install-dir self-deletion via cmd.exe /c
[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int Main(string[] args)
    {
        int errors = 0;
        string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string watchdogExe = Path.Combine(installDir, "WKVRCProxy.exe");

        try { CloseRunningWatchdog(); }
        catch (Exception ex) { Console.Error.WriteLine("close-watchdog: " + ex.Message); errors++; }

        try { RestoreYtDlp(installDir); }
        catch (Exception ex) { Console.Error.WriteLine("restore-yt-dlp: " + ex.Message); errors++; }

        try { RemoveHostsEntry(watchdogExe); }
        catch (Exception ex) { Console.Error.WriteLine("remove-hosts: " + ex.Message); errors++; }

        try { WipeLocalAppData(); }
        catch (Exception ex) { Console.Error.WriteLine("wipe-localappdata: " + ex.Message); errors++; }

        try { ScheduleInstallDirDelete(installDir); }
        catch (Exception ex) { Console.Error.WriteLine("schedule-self-delete: " + ex.Message); errors++; }

        Console.WriteLine(errors == 0
            ? "WKVRCProxy uninstalled. The install folder will disappear in a moment."
            : $"Uninstall finished with {errors} non-fatal error(s) — see messages above.");
        return errors == 0 ? 0 : 2;
    }

    private static void CloseRunningWatchdog()
    {
        foreach (var p in Process.GetProcessesByName("WKVRCProxy"))
        {
            try
            {
                if (!p.CloseMainWindow()) p.Kill();
                p.WaitForExit(5000);
            }
            catch { /* best-effort */ }
        }
        Thread.Sleep(500);
    }

    private static void RestoreYtDlp(string installDir)
    {
        string? toolsDir = TryFindVrcTools();
        if (string.IsNullOrEmpty(toolsDir)) return;

        string target = Path.Combine(toolsDir, "yt-dlp.exe");
        string backup = Path.Combine(toolsDir, "yt-dlp-og.exe");
        string bundled = Path.Combine(installDir, "tools", "yt-dlp-og-fallback.exe");

        if (File.Exists(backup))
        {
            try { if (File.Exists(target)) File.Delete(target); } catch { /* ignore */ }
            File.Move(backup, target);
            return;
        }
        // Belt-and-suspenders: og went missing — drop the bundled vanilla in so
        // the user is left with a working yt-dlp.exe regardless.
        if (File.Exists(bundled))
        {
            File.Copy(bundled, target, true);
        }
    }

    private static void RemoveHostsEntry(string watchdogExe)
    {
        if (!File.Exists(watchdogExe)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = watchdogExe,
                Arguments = "--remove-hosts-entry",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("UAC declined — hosts entry left in place. Remove it manually if desired.");
        }
    }

    private static void WipeLocalAppData()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WKVRCProxy");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static void ScheduleInstallDirDelete(string installDir)
    {
        // Spawn detached cmd.exe that waits, then rmdir's the install dir.
        // The uninstaller exits before the wait elapses so its own exe is no
        // longer locked.
        string cmd = $"/c ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"{installDir}\"";
        var psi = new ProcessStartInfo("cmd.exe", cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        Process.Start(psi);
    }

    private static string? TryFindVrcTools()
    {
        string p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
            "VRChat", "VRChat", "Tools");
        return Directory.Exists(p) ? p : null;
    }
}
