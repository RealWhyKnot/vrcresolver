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
            lock (_writeLock)
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
                w.WriteLine();
                w.WriteLine(ex?.ToString() ?? "(no exception object)");
            }
        }
        catch
        {
            // Crash-log handler is the last line of defense; it must never
            // throw further. Best-effort and move on.
        }
    }
}
