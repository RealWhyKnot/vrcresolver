using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string MutexName = "Global\\WKVRCProxy.Watchdog";

    // Signal-handler-visible state. Components are written from RunWatchdog's
    // construction sequence and read from the signal handlers, which can fire
    // at ANY point — even before those components are constructed. Every read
    // is null-tolerant.
    private static LocalIpcServer? s_ipc;
    private static MeshClient? s_mesh;
    private static PatchManager? s_patcher;
    private static readonly ManualResetEventSlim s_quitSignal = new(false);
    private static volatile bool s_fastShutdown;

    // SetConsoleCtrlHandler: catches CTRL_CLOSE_EVENT (X button), CTRL_LOGOFF_EVENT,
    // and CTRL_SHUTDOWN_EVENT — none of which fire Console.CancelKeyPress. Without
    // this, closing the console window terminated the process with the patch in
    // place and no clean_exit.flag, leaving Tools/yt-dlp.exe pointing at the
    // patched binary until the next launch's recovery ran.
    [DllImport("Kernel32")]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);
    private delegate bool ConsoleCtrlDelegate(uint ctrlType);
    private const uint CTRL_C_EVENT = 0;
    private const uint CTRL_BREAK_EVENT = 1;
    private const uint CTRL_CLOSE_EVENT = 2;
    private const uint CTRL_LOGOFF_EVENT = 5;
    private const uint CTRL_SHUTDOWN_EVENT = 6;

    // Must be a static field, not a local — Windows captures the delegate by
    // function pointer and requires it to outlive the SetConsoleCtrlHandler
    // call. A local would be eligible for GC.
    private static readonly ConsoleCtrlDelegate s_ctrlHandler = OnConsoleCtrl;

    private static int Main(string[] args)
    {
        // Force UTF-8 console output so em-dashes and other non-ASCII characters
        // in log messages render correctly even on Windows hosts where the
        // manifest's activeCodePage hint isn't honored (older builds, redirected
        // stdout, child consoles inheriting a legacy codepage).
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* best-effort */ }

        // Install the rolling file logger so every Console.WriteLine also lands
        // in %LOCALAPPDATA%\WKVRCProxy\logs\watchdog-<utc>.log. Bug reports
        // become "attach this file" instead of "paste your scrollback".
        Logger.Install("watchdog");

        // Anonymous failure reporting (default off; enable with
        // WKVRCPROXY_ANONYMOUS_REPORTING=1). Initialized early so the
        // opt-in banner prints alongside the rest of startup logging.
        ReportingService.Initialize();

        // Install the crash logger so any unhandled exception from this point on
        // lands on disk instead of scrolling off the console. Idempotent — safe
        // even on the elevated re-exec branch.
        CrashHandler.Install("watchdog");

        // Internal re-exec: hosts add/remove from elevated child. Both branches
        // run before mutex acquisition because they exit before the watchdog starts.
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case HostsManager.AddArg: return HostsManager.RunAddInElevatedChild();
                case HostsManager.RemoveArg: return HostsManager.RunRemoveInElevatedChild();
            }
        }

        // Mutex acquisition can throw on locked-down systems (RDP user
        // sessions without SeCreateGlobalPrivilege, hardened security
        // policies). Catch so we can fall back to a Local\ mutex which
        // doesn't require the privilege — single-instance per user
        // session is still useful even if global isn't allowed.
        System.Threading.Mutex? mutex = null;
        try
        {
            try { mutex = new System.Threading.Mutex(false, MutexName, out _); }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("[warn] could not create global mutex (locked-down session?) — using session-local mutex.");
                mutex = new System.Threading.Mutex(false, "Local\\WKVRCProxy.Watchdog", out _);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Mutex creation failed: " + ex.GetType().Name + ": " + ex.Message);
                return 3;
            }

            bool acquired = false;
            try { acquired = mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { acquired = true; }

            if (!acquired)
            {
                Console.WriteLine("WKVRCProxy is already running.");
                return 1;
            }

            try
            {
                return RunWatchdog();
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { /* ignore */ }
            }
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    private static int RunWatchdog()
    {
        // Register signal handlers FIRST — before any setup work. A Ctrl+C
        // (or a console-X-button close) during HostsManager's UAC prompt or
        // during PatchManager construction would otherwise kill the process
        // with no chance to write clean_exit.flag. The handlers are safe to
        // fire while s_ipc / s_mesh / s_patcher are still null.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            s_quitSignal.Set();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // ProcessExit fires on graceful CLR shutdown. The CLR gives us about
            // 2 seconds before tearing the process down — request a fast shutdown
            // so the patcher's atomic restore gets priority over ipc/mesh.
            s_fastShutdown = true;
            s_quitSignal.Set();
            // Block briefly so the runtime's grace window gets used for shutdown.
            try { RunShutdown().Wait(TimeSpan.FromMilliseconds(2000)); }
            catch { /* best-effort */ }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            // Belt-and-suspenders: most unhandled exceptions also fire ProcessExit
            // afterward, but some fatal-fatal kinds (StackOverflow, AccessViolation,
            // ExecutionEngineException) skip ProcessExit and tear the process down
            // directly. Run cleanup here too. RunShutdown is idempotent — if
            // ProcessExit fires later, the second call is a no-op.
            s_fastShutdown = true;
            try { RunShutdown().Wait(TimeSpan.FromSeconds(3)); }
            catch { /* best-effort */ }
        };
        SetConsoleCtrlHandler(s_ctrlHandler, true);

        // Hook a state snapshot into the crash logger so postmortems carry
        // enough watchdog context (mesh state, patch state, pending request
        // count) to diagnose without the live process. Best-effort — runs
        // inside the crash handler's try/catch.
        CrashHandler.SetStateSnapshot(SnapshotState);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        string installDir = AppContext.BaseDirectory;
        string stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WKVRCProxy");
        string vrcToolsDir = VrcPathLocator.Find() ?? "<not found — launch VRChat once>";

        Console.WriteLine($"WKVRCProxy {version}");
        Console.WriteLine($"  install:   {installDir}");
        Console.WriteLine($"  vrc tools: {vrcToolsDir}");
        Console.WriteLine($"  state:     {stateDir}");
        Console.WriteLine($"  os:        {RuntimeInformation.OSDescription}");
        Console.WriteLine($"  runtime:   {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine();

        // Patch the Tools dir FIRST so VRChat sees the patched yt-dlp within
        // ~200 ms of launch. The hosts entry only matters for public-instance
        // support and the UAC prompt can sit unanswered for up to a minute —
        // pushing it after patch engagement means the watchdog is fully
        // operational before the user gets a chance to interact with the
        // dialog.
        s_patcher = new PatchManager(installDir);
        s_patcher.RecoverFromUncleanShutdown();

        s_mesh = new MeshClient();
        s_ipc = new LocalIpcServer(s_mesh);
        s_ipc.Start();
        _ = s_mesh.StartAsync();

        if (!s_patcher.Start())
        {
            RunShutdown().GetAwaiter().GetResult();
            return 2;
        }

        Console.WriteLine("Patch applied. Watching for VRChat overwrites — Ctrl+C to quit.");
        Console.WriteLine("To uninstall, run WKVRCProxy.Uninstaller.exe (in the same folder).");

        // Hosts entry (for public-instance support) on a background task so
        // the UAC prompt doesn't gate the watchdog. Patching is already live
        // at this point; the prompt + elevated child are advisory.
        _ = Task.Run(() =>
        {
            try { HostsManager.EnsureBypassEntryOrPrompt(); }
            catch (Exception ex) { Console.WriteLine("[hosts] background error: " + ex.Message); }
        });

        // Best-effort GitHub releases check; prints one line if a newer
        // version exists so users running the watchdog see the upgrade
        // prompt without having to remember to run the Updater manually.
        UpdateCheck.StartBackgroundCheck();

        // Silent codec install (AV1 / HEVC / VP9) for AVPro decode support.
        // State cached so a successful install or recent failed attempt
        // doesn't re-trigger on every boot.
        CodecInstaller.StartBackgroundCheck();

        // Keep the bundled vanilla yt-dlp at <install>/tools/yt-dlp-og-fallback.exe
        // current with upstream releases. Once per 24 h. Never touches
        // VRChat's pinned yt-dlp-og.exe nor our patched yt-dlp.exe wrapper.
        YtDlpUpdater.StartBackgroundCheck();

        s_quitSignal.Wait();
        Console.WriteLine("Shutting down…");

        RunShutdown().GetAwaiter().GetResult();
        return 0;
    }

    private static bool OnConsoleCtrl(uint ctrlType)
    {
        switch (ctrlType)
        {
            case CTRL_C_EVENT:
            case CTRL_BREAK_EVENT:
                // CancelKeyPress already handles these; signal redundantly in case
                // the registration ordering ever drifts.
                s_quitSignal.Set();
                return false; // let CancelKeyPress run

            case CTRL_CLOSE_EVENT:
            case CTRL_LOGOFF_EVENT:
            case CTRL_SHUTDOWN_EVENT:
                // Windows gives a console-X handler ~5s before SIGKILL. There's
                // no time to politely close the WS or drain the pipe — what
                // actually matters is restoring yt-dlp.exe. Skip ipc + mesh,
                // run patcher.StopAsync (which is fast: cancel loop, atomic
                // restore, write clean_exit.flag).
                s_fastShutdown = true;
                s_quitSignal.Set();
                try { RunShutdown().Wait(TimeSpan.FromMilliseconds(4500)); }
                catch { /* best-effort */ }
                return true; // we handled it
        }
        return false;
    }

    // Shutdown order per the brief: stop pipe accepting → fail pending TCS to
    // server_unreachable (handled inside MeshClient.StopAsync) → close WS clean
    // → stop PatchManager loop → restore yt-dlp.exe → write clean-exit flag.
    // Total budget 12s normally, 4s on console-close fast path. Idempotent —
    // safe to call from both the main path and a signal handler.
    private static int s_shutdownStarted; // 0 = not started, 1 = running/done
    private static async Task RunShutdown()
    {
        if (Interlocked.Exchange(ref s_shutdownStarted, 1) != 0) return;

        var sw = Stopwatch.StartNew();
        bool fast = s_fastShutdown;
        var totalBudget = fast ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(12);

        async Task WithTimeout(Task t, int ms, string step)
        {
            using var cts = new CancellationTokenSource(ms);
            var done = await Task.WhenAny(t, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
            if (done != t) Console.WriteLine("[shutdown] " + step + " exceeded budget — moving on");
        }

        // Skip ipc + mesh on the fast path so the patcher restore gets the
        // entire 4s window. The OS will tear down sockets and pipes when the
        // process exits — they don't need clean shutdown to stay correct.
        if (!fast)
        {
            if (s_ipc != null)
            {
                try
                {
                    int remain = (int)Math.Max(0, (totalBudget - sw.Elapsed).TotalMilliseconds);
                    await WithTimeout(s_ipc.StopAsync(), Math.Min(remain, 3000), "ipc").ConfigureAwait(false);
                }
                catch (Exception ex) { Console.WriteLine("[shutdown] ipc: " + ex.Message); }
            }

            if (s_mesh != null)
            {
                try
                {
                    int remain = (int)Math.Max(0, (totalBudget - sw.Elapsed).TotalMilliseconds);
                    await WithTimeout(s_mesh.StopAsync(), Math.Min(remain, 3000), "mesh").ConfigureAwait(false);
                }
                catch (Exception ex) { Console.WriteLine("[shutdown] mesh: " + ex.Message); }
            }
        }

        if (s_patcher != null)
        {
            try
            {
                int remain = (int)Math.Max(0, (totalBudget - sw.Elapsed).TotalMilliseconds);
                int patcherBudget = fast ? Math.Max(remain, 3000) : Math.Min(remain, 5000);
                await WithTimeout(s_patcher.StopAsync(), patcherBudget, "patcher").ConfigureAwait(false);
            }
            catch (Exception ex) { Console.WriteLine("[shutdown] patcher: " + ex.Message); }
        }
    }

    // CrashHandler-invoked snapshot. Reads loosely — fields are non-null
    // checked but not locked since this runs DURING a crash and any state
    // we observe is best-effort. The snapshot is wrapped in try/catch by
    // CrashHandler, so even a partial throw surfaces a useful postmortem.
    private static string SnapshotState()
    {
        var sb = new System.Text.StringBuilder();
        var mesh = s_mesh;
        if (mesh != null)
        {
            sb.AppendLine("mesh:    connected=" + mesh.IsConnected
                + " server_protocol_version=" + mesh.ServerProtocolVersion
                + " node=" + (mesh.ServerNode ?? "?")
                + " warp_active=" + (mesh.WarpActive?.ToString() ?? "?"));
        }
        else
        {
            sb.AppendLine("mesh:    <not constructed>");
        }
        var patcher = s_patcher;
        if (patcher != null)
        {
            sb.AppendLine("patch:   halted=" + patcher.Halted
                + " vrcToolsDir=" + (patcher.VrcToolsDir ?? "<null>"));
        }
        else
        {
            sb.AppendLine("patch:   <not constructed>");
        }
        sb.AppendLine("ipc:     " + (s_ipc != null ? "<running>" : "<not constructed>"));
        sb.AppendLine("shutdown_started=" + s_shutdownStarted + " fast_shutdown=" + s_fastShutdown);
        return sb.ToString();
    }
}
