using System.Diagnostics;
using System.Runtime.Versioning;
using WKVRCProxy.Shared;

namespace WKVRCProxy.Uninstaller;

// No flags. No prompt. Running this exe IS consent.
//
// 1. Close any running WKVRCProxy.exe (only ones launched from THIS install
//    dir — parallel installs in other dirs are left alone)
// 2. Restore yt-dlp.exe from yt-dlp-og.exe in VRChat Tools (belt-and-
//    suspenders: drop the bundled vanilla in if og went missing; warn if
//    even the bundled fallback is absent so VRChat won't be left with our
//    patched yt-dlp pointing at a soon-to-be-deleted install dir)
// 3. Remove the hosts entry (UAC re-exec via WKVRCProxy.exe
//    --remove-hosts-entry, but only if the entry is actually present —
//    avoids a UAC prompt for users who never enabled public-instance mode)
// 4. Remove the localhost.youtube.com HTTPS cert/bindings if present
// 5. Wipe %LOCALAPPDATA%Low\WKVRCProxy\ (current state root) AND the
//    legacy %LOCALAPPDATA%\WKVRCProxy\ tree
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
        WkvrcPaths.MigrateLegacyState(Console.WriteLine);
        Logger.Install("uninstaller");
        CrashHandler.Install("uninstaller");
        int errors = 0;
        string installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string watchdogExe = Path.Combine(installDir, "WKVRCProxy.exe");

        Console.WriteLine("[uninstall] start installDir=" + installDir);

        errors += RunStep("close-watchdog", () => CloseRunningWatchdog(installDir));
        errors += RunStep("restore-yt-dlp", () => RestoreYtDlp(installDir));
        errors += RunStep("remove-hosts", () => RemoveHostsEntry(watchdogExe));
        errors += RunStep("remove-relay-tls", () => RemoveRelayTls(watchdogExe));
        errors += RunStep("wipe-state", WipeState);
        errors += RunStep("schedule-self-delete", () => ScheduleInstallDirDelete(installDir));

        Console.WriteLine(errors == 0
            ? "WKVRCProxy uninstalled. The install folder will disappear in a moment."
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
    // dir's WKVRCProxy.exe. A user with parallel installs (release + dev,
    // or two VRChat profiles) would otherwise have the OTHER install's
    // watchdog killed when uninstalling one — hard to diagnose, easy to
    // avoid. MainModule.FileName lookup throws on processes the current
    // user can't open (admin-elevated watchdog from a non-admin uninstall);
    // we treat those as "not ours" rather than failing the step.
    private static void CloseRunningWatchdog(string installDir)
    {
        string ownExe = Path.Combine(installDir, "WKVRCProxy.exe");
        int closed = 0, skipped = 0;
        foreach (var p in Process.GetProcessesByName("WKVRCProxy"))
        {
            using (p)
            {
                string? procExe = null;
                try { procExe = p.MainModule?.FileName; }
                catch { /* probably elevated and we're not */ skipped++; continue; }
                if (procExe == null
                    || !string.Equals(Path.GetFullPath(procExe), Path.GetFullPath(ownExe),
                        StringComparison.OrdinalIgnoreCase))
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

    private static void RestoreYtDlp(string installDir)
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
                return;
            }
            // Both backup AND bundled fallback missing. If yt-dlp.exe exists in
            // VRChat's Tools dir it's almost certainly OUR patched wrapper —
            // pointing at an install dir we're about to delete. VRChat will
            // hard-fail every video play once the install is gone with no
            // clear diagnostic. Surface a loud warning so the user knows what
            // to do (let VRChat re-download yt-dlp on next launch).
            if (File.Exists(target))
            {
                Console.Error.WriteLine(
                    "[uninstall] WARNING: VRChat Tools/yt-dlp.exe could not be restored to vanilla — "
                    + "neither yt-dlp-og.exe nor the bundled fallback at " + bundled + " was available. "
                    + "Delete VRChat Tools/yt-dlp.exe manually so VRChat re-downloads it on next launch, "
                    + "or reinstall VRChat.");
                throw new InvalidOperationException(
                    "Tools/yt-dlp.exe could not be restored — backup and bundled fallback both missing");
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

    // Wipe BOTH the current state root (LocalLow, where everything lives
    // post-integrity-fix) AND the legacy %LOCALAPPDATA%\WKVRCProxy\ tree
    // (in case migration was incomplete or never ran). Earlier impl only
    // wiped the legacy path — every uninstall left the user's logs,
    // crashes, codec-state.json, and update-check JSON behind on the
    // LocalLow side. Confirmed bug.
    //
    // Closes the open log writer first so the BaseStream's FileShare.Read
    // handle doesn't block Directory.Delete on the logs subdir. After
    // close, Tee() is a no-op — subsequent Console.WriteLine still reaches
    // the underlying console writer for the user's "uninstalled" banner.
    private static void WipeState()
    {
        Logger.Close();

        int wiped = 0;
        string lowRoot = WkvrcPaths.StateRoot();
        if (Directory.Exists(lowRoot))
        {
            try { Directory.Delete(lowRoot, recursive: true); wiped++; }
            catch (Exception ex) { Console.Error.WriteLine("[uninstall] could not wipe " + lowRoot + ": " + ex.Message); }
        }

        string legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WKVRCProxy");
        if (Directory.Exists(legacyRoot))
        {
            try { Directory.Delete(legacyRoot, recursive: true); wiped++; }
            catch (Exception ex) { Console.Error.WriteLine("[uninstall] could not wipe " + legacyRoot + ": " + ex.Message); }
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
            "WKVRCProxy-uninstall-rmdir-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".log");
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
