using System.Diagnostics;
using System.Runtime.Versioning;
using VrcResolver.Shared;

namespace VrcResolver.Uninstaller;

// No flags. No prompt. Running this exe IS consent.
//
// 1. Close any running watchdog (only ones launched from THIS install
//    dir — parallel installs in other dirs are left alone). Pre-rename
//    process/exe names are matched too.
// 2. Restore yt-dlp.exe from yt-dlp-og.exe in VRChat Tools (belt-and-
//    suspenders: drop the bundled vanilla in if og went missing; warn if
//    even the bundled fallback is absent so VRChat won't be left with our
//    patched yt-dlp pointing at a soon-to-be-deleted install dir)
// 3. Remove the hosts entry (UAC re-exec via vrcresolver.exe
//    --remove-hosts-entry, but only if the entry is actually present —
//    avoids a UAC prompt for users who never enabled public-instance mode)
// 4. Remove the localhost.youtube.com HTTPS cert/bindings if present
// 5. Wipe the state roots: the current LocalLow root, the pre-rename
//    LocalLow root, the even-older %LOCALAPPDATA% tree, and both
//    ProgramData dirs
// 6. Schedule install-dir self-deletion via cmd.exe /c, capturing the
//    rmdir output to %TEMP% so a stuck rmdir leaves a diagnostic trail
//
// Per-step start/ok/skipped breadcrumbs are emitted to the rolling log so
// "uninstall left X behind" reports can be diagnosed without a repro.
[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* best-effort */ }
        AppPaths.MigrateFromLegacyProduct(Console.WriteLine);
        Logger.Install("uninstaller");
        CrashHandler.Install("uninstaller");
        int errors = 0;
        string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string watchdogExe = Path.Combine(installDir, "vrcresolver.exe");

        Console.WriteLine("[uninstall] start installDir=" + installDir);

        errors += RunStep("close-watchdog", () => CloseRunningWatchdog(installDir));
        errors += RunStep("restore-yt-dlp", RestoreYtDlp);
        errors += RunStep("remove-hosts", () => RemoveHostsEntry(watchdogExe));
        errors += RunStep("remove-relay-tls", () => RemoveRelayTls(watchdogExe));
        errors += RunStep("wipe-state", WipeState);
        errors += RunStep("schedule-self-delete", () => ScheduleInstallDirDelete(installDir));

        Console.WriteLine(errors == 0
            ? "VRCResolver uninstalled. The install folder will disappear in a moment."
            : $"Uninstall finished with {errors} non-fatal error(s) — see messages above.");
        return errors == 0 ? 0 : 2;
    }

    // Per-step wrapper: emits start/ok/error breadcrumbs to the log so the
    // remote postmortem can see which step ran, in what order, and whether
    // it skipped vs threw. Returns 1 on caught exception so the caller can
    // accumulate an error count.
    private static int RunStep(string step, Action body)
    {
        Console.WriteLine("[uninstall] " + step + " start");
        try
        {
            body();
            Console.WriteLine("[uninstall] " + step + " ok");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[uninstall] " + step + " ERROR " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    // Only close watchdog processes that were launched from THIS install
    // dir's watchdog exe (current or pre-rename name). A user with parallel
    // installs (release + dev, or two VRChat profiles) would otherwise have
    // the OTHER install's watchdog killed when uninstalling one — hard to
    // diagnose, easy to avoid. MainModule.FileName lookup throws on
    // processes the current user can't open (admin-elevated watchdog from a
    // non-admin uninstall); we treat those as "not ours" rather than
    // failing the step.
    private static void CloseRunningWatchdog(string installDir)
    {
        string[] ownExes =
        {
            Path.GetFullPath(Path.Combine(installDir, "vrcresolver.exe")),
            Path.GetFullPath(Path.Combine(installDir, "WKVRCProxy.exe")),
        };
        int closed = 0, skipped = 0;
        var procs = Process.GetProcessesByName("vrcresolver")
            .Concat(Process.GetProcessesByName("WKVRCProxy"));
        foreach (var p in procs)
        {
            using (p)
            {
                string? procExe = null;
                try { procExe = p.MainModule?.FileName; }
                catch { /* probably elevated and we're not */ skipped++; continue; }
                if (procExe == null
                    || !ownExes.Any(own => string.Equals(Path.GetFullPath(procExe), own,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    if (!p.CloseMainWindow()) p.Kill();
                    p.WaitForExit(5000);
                    closed++;
                }
                catch { /* best-effort */ }
            }
        }
        Console.WriteLine("[uninstall] close-watchdog matched=" + closed + " skipped_other_installs=" + skipped);
        if (closed > 0) Thread.Sleep(500);
    }

    private static void RestoreYtDlp()
    {
        string? toolsDir = TryFindVrcTools();
        if (string.IsNullOrEmpty(toolsDir))
        {
            Console.WriteLine("[uninstall] restore-yt-dlp skipped: VRChat Tools dir not found");
            return;
        }

        // Sweep before AND after: clears any sidecars from prior unclean runs
        // up front, and clears the .stale-<utc> we may produce ourselves below.
        try { ToolsDirSweeper.Sweep(toolsDir); } catch { /* best-effort */ }

        string target = Path.Combine(toolsDir, "yt-dlp.exe");
        string backup = Path.Combine(toolsDir, "yt-dlp-og.exe");

        try
        {
            if (File.Exists(backup))
            {
                // Atomic same-volume rename -- no window where yt-dlp.exe is
                // missing while the move is in flight. Falls back to the
                // move-aside-then-move pattern if the target is locked
                // (VRChat / AV holding it).
                try
                {
                    File.Move(backup, target, overwrite: true);
                    return;
                }
                catch (IOException)
                {
                    /* target locked -- try move-aside path below */
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
            // Backup missing. If yt-dlp.exe exists in VRChat's Tools dir
            // it's almost certainly OUR wrapper -- pointing at an install
            // dir we're about to delete. Delete the wrapper so VRChat
            // re-downloads its yt-dlp on next session; safer than leaving
            // a broken wrapper that exec's into nothing.
            if (File.Exists(target))
            {
                try
                {
                    File.Delete(target);
                    Console.WriteLine(
                        "[uninstall] yt-dlp-og.exe was missing; deleted Tools/yt-dlp.exe so VRChat redownloads its yt-dlp on next session.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        "[uninstall] WARNING: could not delete Tools/yt-dlp.exe: " + ex.Message + " -- "
                        + "delete it manually so VRChat re-downloads on next launch.");
                    throw new InvalidOperationException(
                        "Tools/yt-dlp.exe could not be removed -- backup missing and delete failed");
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
        if (!File.Exists(watchdogExe))
        {
            Console.WriteLine("[uninstall] remove-hosts skipped: watchdog exe missing (can't re-exec elevated)");
            return;
        }
        // Skip the UAC prompt entirely if no entry is present — users who
        // never enabled public-instance mode shouldn't see a UAC dialog
        // for a no-op write. The hosts file is world-readable so we can
        // check from this unelevated process.
        if (!HostsFileContainsBypassEntry())
        {
            Console.WriteLine("[uninstall] remove-hosts skipped: entry already absent");
            return;
        }
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
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("UAC declined — hosts entry left in place. Remove it manually if desired.");
        }
    }

    private static void RemoveRelayTls(string watchdogExe)
    {
        if (!File.Exists(watchdogExe))
        {
            Console.WriteLine("[uninstall] remove-relay-tls skipped: watchdog exe missing (can't re-exec elevated)");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = watchdogExe,
                Arguments = "--local-relay-tls-remove",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
            if (proc != null && !proc.HasExited)
                Console.WriteLine("[uninstall] remove-relay-tls elevation child still running after 30s");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("UAC declined -- localhost.youtube.com HTTPS certificate/bindings may be left in place.");
        }
    }

    // Local read-only check that mirrors HostsManager.IsBypassActive (we
    // can't reference the watchdog assembly's internal class from here).
    // Match: a non-comment line containing both "127.0.0.1" and the marker
    // host. Best-effort — failures (file missing, locked) return false so
    // the caller falls through to the UAC re-exec which has its own
    // error handling.
    private const string BypassMarkerHost = "localhost.youtube.com";
    private static bool HostsFileContainsBypassEntry()
    {
        string p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers", "etc", "hosts");
        if (!File.Exists(p)) return false;
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                string t = line.Trim();
                if (t.StartsWith('#')) continue;
                if (t.Contains("127.0.0.1") && t.Contains(BypassMarkerHost)) return true;
            }
        }
        catch { /* best-effort */ }
        return false;
    }

    // Wipe every state root this product has ever used: the current
    // LocalLow root, the pre-rename LocalLow root (left in place by the
    // rename migration for the wrapper transition window), the even-older
    // %LOCALAPPDATA% tree, and both ProgramData dirs (TLS ports file;
    // best-effort — files written by the elevated bootstrap may need the
    // elevated remove-relay-tls step, which already ran).
    //
    // Closes the open log writer first so the BaseStream's FileShare.Read
    // handle doesn't block Directory.Delete on the logs subdir. After
    // close, Tee() is a no-op — subsequent Console.WriteLine still reaches
    // the underlying console writer for the user's "uninstalled" banner.
    private static void WipeState()
    {
        Logger.Close();

        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string legacyLowRoot = Path.Combine(
            Path.GetDirectoryName(AppPaths.StateRoot()) ?? (localApp + "Low"),
            LegacyCompat.LegacyStateDirName);
        string[] roots =
        {
            AppPaths.StateRoot(),
            legacyLowRoot,
            Path.Combine(localApp, LegacyCompat.LegacyStateDirName),
            AppPaths.ProgramDataRoot(),
            Path.Combine(programData, LegacyCompat.LegacyStateDirName),
        };

        int wiped = 0;
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try { Directory.Delete(root, recursive: true); wiped++; }
            catch (Exception ex) { Console.Error.WriteLine("[uninstall] could not wipe " + root + ": " + ex.Message); }
        }

        Console.WriteLine("[uninstall] wipe-state directories_wiped=" + wiped);
    }

    private static void ScheduleInstallDirDelete(string installDir)
    {
        // Defensive: refuse to interpolate a path containing a `"` character.
        // AppContext.BaseDirectory cannot reach this state on Windows (file
        // APIs reject quotes in path segments), but bail loudly rather than
        // emitting malformed cmd that could be mistakenly parsed.
        if (installDir.Contains('"'))
            throw new InvalidOperationException("install dir contains a quote character: " + installDir);

        // Spawn detached cmd.exe that waits, then rmdir's the install dir.
        // The uninstaller exits before the wait elapses so its own exe is
        // no longer locked. 3-second wait is conservative — earlier 1s wait
        // raced against AV scanners holding the exe handle on slow disks.
        // Capture rmdir output to %TEMP% so a stuck rmdir leaves a
        // diagnostic trail (otherwise the user sees "uninstalled" but the
        // dir survives, with no clue why).
        string log = Path.Combine(Path.GetTempPath(),
            "vrcresolver-uninstall-rmdir-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".log");
        string cmd = $"/c (ping 127.0.0.1 -n 4 > nul) & (rmdir /s /q \"{installDir}\") > \"{log}\" 2>&1";
        var psi = new ProcessStartInfo("cmd.exe", cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        Process.Start(psi);
        Console.WriteLine("[uninstall] schedule-self-delete cmd-log=" + log);
    }

    private static string? TryFindVrcTools()
    {
        string p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
            "VRChat", "VRChat", "Tools");
        return Directory.Exists(p) ? p : null;
    }
}
