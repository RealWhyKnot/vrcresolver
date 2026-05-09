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
    private static VrcLogMonitor? s_logmon;
    private static HostsTicker? s_hostsTicker;
    private static Heartbeat? s_heartbeat;
    private static ResolveCache? s_resolveCache;
    private static RelayPortManager? s_relayPort;
    private static LocalRelayServer? s_relay;
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

        // Migrate any state from the legacy %LOCALAPPDATA%\WKVRCProxy\
        // location to the LocalLow root before any logger / crash handler
        // tries to open files. Idempotent — runs only on first launch
        // after the integrity fix landed.
        WkvrcPaths.MigrateLegacyState(Console.WriteLine);

        // Install the rolling file logger so every Console.WriteLine also lands
        // in %LOCALAPPDATA%Low\WKVRCProxy\logs\watchdog-<utc>.log. Bug reports
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
                Console.WriteLine("[warn] could not create global mutex (locked-down session?) -- using session-local mutex.");
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
        string stateDir = WkvrcPaths.StateRoot();
        string vrcToolsDir = VrcPathLocator.Find() ?? "<not found — launch VRChat once>";

        // Pragma: BuildInfo.IsDevBuild is a const, so when build.ps1 stamps
        // it false in a release build the banner branch folds to unreachable.
        // The <TreatWarningsAsErrors> project setting promotes CS0162 to an
        // error; pragma-disable lets the conditional compile cleanly in both
        // modes without resorting to a runtime field that the JIT cannot
        // constant-fold across.
#pragma warning disable CS0162
        bool isDev = BuildInfo.IsDevBuild;
#pragma warning restore CS0162
        ConsoleUx.Banner(
            version: version,
            sha: BuildInfo.GitSha,
            buildTime: BuildInfo.BuildTime,
            isDev: isDev,
            paths: new (string, string)[]
            {
                ("install",   installDir),
                ("vrc tools", vrcToolsDir),
                ("state",     stateDir),
                ("os",        RuntimeInformation.OSDescription),
                ("runtime",   RuntimeInformation.FrameworkDescription),
            });

        // Detect a pre-existing VRChat process so the operator sees up-front
        // whether the patch can apply immediately or whether it will defer
        // until VRChat releases its yt-dlp.exe handle. PID + start time
        // surface so a "deferring" log line later is correlatable to the
        // exact VRChat invocation that's holding the file.
        PatchManager.LogVrcProcessState();

        // Patch the Tools dir FIRST so VRChat sees the patched yt-dlp within
        // ~200 ms of launch. The hosts entry only matters for public-instance
        // support and the UAC prompt can sit unanswered for up to a minute —
        // pushing it after patch engagement means the watchdog is fully
        // operational before the user gets a chance to interact with the
        // dialog.
        s_patcher = new PatchManager(installDir);
        s_patcher.RecoverFromUncleanShutdown();

        s_mesh = new MeshClient();
        // v3.2: persistent resolve cache. ResolveCache loads lazily on
        // first Lookup/Store; passing the same instance to LocalIpcServer
        // (writer + reader) and VrcLogMonitor (evict-on-stall) keeps the
        // in-memory dict authoritative.
        s_resolveCache = new ResolveCache();
        s_ipc = new LocalIpcServer(s_mesh, s_resolveCache);
        s_ipc.Start();
        _ = s_mesh.StartAsync();

        // Local-relay HTTP listener (Phase 1 trust gateway). Binds 127.0.0.1
        // on an ephemeral high port; the patched yt-dlp wrapper reads the
        // port file and rewrites resolved URLs to
        // `http://localhost.youtube.com:{port}/play/<session>/manifest.<ext>?target=<base64>`
        // so AVPro's allowlist (which has *.youtube.com) accepts them in
        // default-public worlds. The relay forwards bytes to WhyKnot.dev
        // without parsing manifests; WhyKnot.dev owns compatibility and
        // transcode decisions. Failure to bind is non-fatal: the wrapper
        // falls through to emitting the raw server URL on missing port file.
        // HTTPS + per-machine cert lifecycle is a planned follow-up.
        s_relayPort = new RelayPortManager();
        if (s_relayPort.Initialize())
        {
            try
            {
                s_relay = new LocalRelayServer(s_relayPort.CurrentPort);
                s_relay.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[relay][warn] listener start failed: " + ex.Message
                    + " -- public-instance trust gateway disabled");
                s_relay = null;
                s_relayPort.DeletePortFile();
            }
        }
        else
        {
            Console.WriteLine("[relay][warn] could not allocate ephemeral port -- public-instance trust gateway disabled");
        }

        // Watch VRChat's output_log_*.txt for AVPro playback failures and
        // forward as `playback_feedback` mesh frames. Server-side dispatch
        // uses these to demote whichever strategy/config produced a URL
        // AVPro couldn't actually load. Also wired to ResolveCache so a
        // load_failure / silent_stall on a cached URL evicts the cached
        // entry, closing the staleness-detection loop without server help.
        s_logmon = new VrcLogMonitor(s_mesh, s_resolveCache);
        s_logmon.Start();

        if (!s_patcher.Start())
        {
            RunShutdown().GetAwaiter().GetResult();
            return 2;
        }

        Console.WriteLine("Patch applied. Watching for VRChat overwrites -- Ctrl+C to quit.");
        Console.WriteLine("To uninstall, run WKVRCProxy.Uninstaller.exe (in the same folder).");

        // Hosts entry (for public-instance support) on a background task so
        // the UAC prompt doesn't gate the watchdog. Patching is already live
        // at this point; the prompt + elevated child are advisory. The
        // periodic HostsTicker takes over after this initial add — re-checks
        // every minute and re-adds if the entry disappears (manual edit, AV
        // rewrite, etc.).
        _ = Task.Run(() =>
        {
            try { HostsManager.EnsureBypassEntryOrPrompt(); }
            catch (Exception ex) { Console.WriteLine("[hosts][warn] background error: " + ex.Message); }
        });

        s_hostsTicker = new HostsTicker();
        s_hostsTicker.Start();

        // Periodic "still alive" line + aggregate stats (resolves, lh-yt
        // count, stream bytes, reconnects). Suppresses itself when other
        // logging is active so it doesn't spam a busy console.
        s_heartbeat = new Heartbeat(s_mesh, s_resolveCache);
        s_heartbeat.Start();

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
        Console.WriteLine("Shutting down...");

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
            if (done != t) Console.WriteLine("[shutdown][warn] " + step + " exceeded budget -- moving on");
        }

        // Skip ipc + mesh + logmon on the fast path so the patcher restore
        // gets the entire 4s window. The OS will tear down sockets, pipes,
        // and file handles when the process exits — they don't need clean
        // shutdown to stay correct.
        if (!fast)
        {
            if (s_heartbeat != null)
            {
                try
                {
                    int remain = (int)Math.Max(0, (totalBudget - sw.Elapsed).TotalMilliseconds);
                    await WithTimeout(s_heartbeat.StopAsync(), Math.Min(remain, 500), "heartbeat").ConfigureAwait(false);
                }
                catch (Exception ex) { Console.WriteLine("[shutdown][warn] heartbeat: " + ex.Message); }
            }

            if (s_hostsTicker != null)
            {
                try
                {
                    int remain = (int)Math.Max(0, (totalBudget - sw.Elapsed).TotalMilliseconds);
                    await WithTimeout(s_hostsTicker.StopAsync(), Math.Min(remain, 500), "hosts-ticker").ConfigureAwait(false);
                }
                catch (Exception ex) { Console.WriteLine("[shutdown][warn] hosts-ticker: " + ex.Message); }
            }

            if (s_logmon != null)
            {
                try
                {
                    int remain = (int)Math.Max(0, (totalBudget - sw.Elapsed).TotalMilliseconds);
                    await WithTimeout(s_logmon.StopAsync(), Math.Min(remain, 1000), "logmon").ConfigureAwait(false);
                }
                catch (Exception ex) { Console.WriteLine("[shutdown][warn] logmon: " + ex.Message); }
            }

            if (s_ipc != null)
            {
                try
                {
                    int remain = (int)Math.Max(0, (totalBudget - sw.Elapsed).TotalMilliseconds);
                    await WithTimeout(s_ipc.StopAsync(), Math.Min(remain, 3000), "ipc").ConfigureAwait(false);
                }
                catch (Exception ex) { Console.WriteLine("[shutdown][warn] ipc: " + ex.Message); }
            }

            // Stop the relay listener AFTER ipc but before mesh -- AVPro
            // may still be holding open a streaming connection through us;
            // the listener cancels its CTS on Stop which propagates to the
            // upstream HttpClient request, unwinding the in-flight HLS
            // segment fetch cleanly. Port file gets deleted so the wrapper
            // (started by VRChat AFTER our shutdown) falls through to the
            // raw-URL behavior on its next invocation.
            if (s_relay != null)
            {
                try
                {
                    int remain = (int)Math.Max(0, (totalBudget - sw.Elapsed).TotalMilliseconds);
                    await WithTimeout(s_relay.StopAsync(), Math.Min(remain, 2000), "relay").ConfigureAwait(false);
                    s_relay.Dispose();
                    s_relay = null;
                }
                catch (Exception ex) { Console.WriteLine("[shutdown][warn] relay: " + ex.Message); }
            }
            try { s_relayPort?.DeletePortFile(); } catch { /* best-effort */ }

            if (s_mesh != null)
            {
                try
                {
                    int remain = (int)Math.Max(0, (totalBudget - sw.Elapsed).TotalMilliseconds);
                    await WithTimeout(s_mesh.StopAsync(), Math.Min(remain, 3000), "mesh").ConfigureAwait(false);
                }
                catch (Exception ex) { Console.WriteLine("[shutdown][warn] mesh: " + ex.Message); }
            }

            // Flush the resolve cache last so any debounced writes from
            // the final pre-shutdown resolves land on disk. Synchronous +
            // best-effort -- if the file system is misbehaving we lose
            // the most recent entries; the next launch rebuilds them.
            if (s_resolveCache != null)
            {
                try { s_resolveCache.FlushNow(); }
                catch (Exception ex) { Console.WriteLine("[shutdown][warn] resolve-cache: " + ex.Message); }
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
            catch (Exception ex) { Console.WriteLine("[shutdown][warn] patcher: " + ex.Message); }
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
