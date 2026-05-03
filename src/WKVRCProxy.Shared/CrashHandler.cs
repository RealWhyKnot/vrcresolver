using System.Reflection;

namespace WKVRCProxy.Shared;

// Hooks AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException
// so unhandled-exception teardowns leave a postmortem on disk instead of just
// scrolling off the console buffer. Crash logs land at
//   %LOCALAPPDATA%\WKVRCProxy\crashes\crash-<component>-<utc>.log
// and contain stack + process metadata. Each exe (watchdog, updater,
// uninstaller) calls Install() at the very top of Main so handlers are live
// before any other code runs.
public static class CrashHandler
{
    private static readonly object _writeLock = new();
    private static string? _logDir;
    private static string _component = "unknown";
    private static int _installed; // Interlocked guard — Install is idempotent.
    private static Func<string>? _stateSnapshot;

    // Optional state-snapshot delegate. Whatever the caller registers gets
    // invoked at crash time and its output is written into the postmortem.
    // The watchdog populates this with a one-paragraph status block (mesh
    // state, patch state, pending request count) so a crash log carries
    // enough context to diagnose without the live process. Best-effort:
    // exceptions inside the snapshot are caught.
    public static void SetStateSnapshot(Func<string>? snapshot)
    {
        _stateSnapshot = snapshot;
    }

    public static void Install(string component)
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        _component = component;

        try
        {
            _logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WKVRCProxy", "crashes");
            Directory.CreateDirectory(_logDir);
        }
        catch
        {
            // Can't create the dir (UAC / disk-full / weird profile). The
            // handlers will silently no-op — at least we won't make things
            // worse during teardown.
            _logDir = null;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteCrashLog("UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception, terminating: false);
            // Mark observed so .NET's legacy fail-fast policy (if ever re-enabled)
            // doesn't kill the process for a logged-and-handled background fault.
            e.SetObserved();
        };
    }

    private static void WriteCrashLog(string kind, Exception? ex, bool terminating)
    {
        if (_logDir == null) return;
        try
        {
            string ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string path = Path.Combine(_logDir, $"crash-{_component}-{ts}.log");
            // TryEnter (not lock) so if another thread is wedged inside the
            // crash handler we don't block process teardown waiting for it.
            // 1s budget is plenty for a single small log write.
            if (!Monitor.TryEnter(_writeLock, TimeSpan.FromSeconds(1))) return;
            try
            {
                using var w = new StreamWriter(path, append: false);
                w.WriteLine("=== WKVRCProxy crash log ===");
                w.WriteLine($"timestamp:    {DateTime.UtcNow:o}");
                w.WriteLine($"component:    {_component}");
                w.WriteLine($"kind:         {kind}");
                w.WriteLine($"terminating:  {terminating}");
                w.WriteLine($"pid:          {Environment.ProcessId}");
                w.WriteLine($"version:      {Assembly.GetEntryAssembly()?.GetName().Version}");
                w.WriteLine($"basedir:      {AppContext.BaseDirectory}");
                w.WriteLine($"os:           {Environment.OSVersion}");

                // Caller-provided state snapshot, e.g. mesh/patch/pending-
                // request status. Wrapped in try/catch so a buggy snapshot
                // delegate can't prevent the exception from being recorded.
                var snapshot = _stateSnapshot;
                if (snapshot != null)
                {
                    w.WriteLine();
                    w.WriteLine("--- state snapshot ---");
                    try { w.WriteLine(snapshot()); }
                    catch (Exception sex) { w.WriteLine("(snapshot delegate threw: " + sex.GetType().Name + ": " + sex.Message + ")"); }
                }

                w.WriteLine();
                w.WriteLine(ex?.ToString() ?? "(no exception object)");
            }
            finally
            {
                Monitor.Exit(_writeLock);
            }
        }
        catch
        {
            // Crash-log handler is the last line of defense; it must never
            // throw further. Best-effort and move on.
        }
    }
}
