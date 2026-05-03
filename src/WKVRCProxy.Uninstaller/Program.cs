using System.Diagnostics;
using System.Runtime.Versioning;
using WKVRCProxy.Shared;

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
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* best-effort */ }
        CrashHandler.Install("uninstaller");
        int errors = 0;
        string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string watchdogExe = Path.Combine(installDir, "WKVRCProxy.exe");

        try { CloseRunningWatchdog(); }
        catch (Exception ex) { LogStepError("close-watchdog", ex); errors++; }

        try { RestoreYtDlp(installDir); }
        catch (Exception ex) { LogStepError("restore-yt-dlp", ex); errors++; }

        try { RemoveHostsEntry(watchdogExe); }
        catch (Exception ex) { LogStepError("remove-hosts", ex); errors++; }

        try { WipeLocalAppData(); }
        catch (Exception ex) { LogStepError("wipe-localappdata", ex); errors++; }

        try { ScheduleInstallDirDelete(installDir); }
        catch (Exception ex) { LogStepError("schedule-self-delete", ex); errors++; }

        Console.WriteLine(errors == 0
            ? "WKVRCProxy uninstalled. The install folder will disappear in a moment."
            : $"Uninstall finished with {errors} non-fatal error(s) — see messages above.");
        return errors == 0 ? 0 : 2;
    }

    // Step-error log helper: preserves exception type alongside the message
    // so a bug report shows whether a "restore-yt-dlp" failure was an
    // UnauthorizedAccessException (permissions), IOException (file locked),
    // FileNotFoundException (target gone), etc.
    private static void LogStepError(string step, Exception ex) =>
        Console.Error.WriteLine(step + ": " + ex.GetType().Name + ": " + ex.Message);

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

        // Sweep before AND after: clears any sidecars from prior unclean runs
        // up front, and clears the .stale-<utc> we may produce ourselves below.
        try { ToolsDirSweeper.Sweep(toolsDir); } catch { /* best-effort */ }

        string target = Path.Combine(toolsDir, "yt-dlp.exe");
        string backup = Path.Combine(toolsDir, "yt-dlp-og.exe");
        string bundled = Path.Combine(installDir, "tools", "yt-dlp-og-fallback.exe");

        try
        {
            if (File.Exists(backup))
            {
                // Atomic same-volume rename — no window where yt-dlp.exe is missing
                // while the move is in flight. Falls back to the move-aside-then-
                // move pattern if the target is locked (VRChat / AV holding it).
                try
                {
                    File.Move(backup, target, overwrite: true);
                    return;
                }
                catch (IOException)
                {
                    /* target locked — try move-aside path below */
                }

                try
                {
                    if (File.Exists(target))
                    {
                        string stale = target + ".stale-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                        File.Move(target, stale);
                    }
                    File.Move(backup, target);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("yt-dlp.exe restore failed: " + ex.Message);
                    throw;
                }
                return;
            }
            // Belt-and-suspenders: og went missing — drop the bundled vanilla in so
            // the user is left with a working yt-dlp.exe regardless. Atomic stage
            // so we never replace a working binary with a half-written copy.
            if (File.Exists(bundled))
            {
                string tmp = target + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try
                {
                    File.Copy(bundled, tmp, overwrite: true);
                    File.Move(tmp, target, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
                    throw;
                }
            }
        }
        finally
        {
            // Final pass — picks up the .stale-<utc> from the locked-target
            // branch (and any .new-<short> from a partial run), so Tools/
            // is left containing only yt-dlp.exe.
            try { ToolsDirSweeper.Sweep(toolsDir); } catch { /* best-effort */ }
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
