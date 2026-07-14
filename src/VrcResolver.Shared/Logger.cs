using System.Text;

namespace VrcResolver.Shared;

// Tees Console.Out AND Console.Error to a rolling file at
//   %LOCALAPPDATA%Low\vrcresolver\logs\<component>-<utc>.log
// Console stays the primary UX; the file is the support artifact users
// can attach to bug reports without scraping their scrollback.
//
// Rotation: a fresh file is opened on Install() (per-process), and again
// whenever the active file passes 10 MiB. Retention: any <component>-*.log
// older than 7 days (mtime) is deleted on Install().
//
// Each exe (watchdog, updater, uninstaller) calls Install at the top of
// Main so its boot output is captured. Close() is exposed for callers that
// need to release the file handle before deleting the log directory (the
// uninstaller's wipe path).
public static class Logger
{
    private const long MaxBytes = 10L * 1024 * 1024;
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);

    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static string? _logDir;
    private static string _component = "unknown";
    private static int _installed;
    private static int _devConsoleDiagnostics;

    // Stamp updated on every Tee write so the watchdog's Heartbeat ticker
    // can suppress its periodic "still alive" line when something else has
    // already logged recently. Volatile read is fine — heartbeat only cares
    // about "was anything logged in the last 5 minutes" coarse-grained.
    private static long _lastWriteTicksUtc;
    public static DateTime LastWriteUtc => new(Volatile.Read(ref _lastWriteTicksUtc), DateTimeKind.Utc);

    public static bool DevConsoleDiagnosticsEnabled => Volatile.Read(ref _devConsoleDiagnostics) != 0;

    public static void SetDevConsoleDiagnostics(bool enabled)
    {
        Volatile.Write(ref _devConsoleDiagnostics, enabled ? 1 : 0);
    }

    public static void Install(string component)
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        _component = component;
        try
        {
            _logDir = AppPaths.LogsDir();
            Directory.CreateDirectory(_logDir);
            PruneOld();
            OpenNew();
        }
        catch
        {
            // Can't create the dir / open a file (UAC / disk-full / weird
            // profile). Console output keeps working — just no file artifact.
            _logDir = null;
            return;
        }

        Console.SetOut(new TeeWriter(Console.Out));
        Console.SetError(new TeeWriter(Console.Error));
    }

    // Releases the open log file handle so a caller (e.g., the uninstaller's
    // WipeLocalAppData step) can delete the logs directory without a sharing
    // violation. After Close, Tee() becomes a no-op — subsequent
    // Console.WriteLine still writes to the underlying console writer.
    // Idempotent.
    public static void Close()
    {
        lock (_lock)
        {
            try { _writer?.Flush(); } catch { /* best-effort */ }
            try { _writer?.Dispose(); } catch { /* best-effort */ }
            _writer = null;
        }
    }

    // Write a line to the rolling log file ONLY — bypassing the console
    // tee. Used for verbose diagnostic streams (per-strategy mesh
    // resolve_log frames, full per-resolve traces, etc.) that should
    // remain available for grep / bug-report attachment but would
    // clutter the user-facing console window.
    public static void WriteFileOnly(string? line)
    {
        Tee(line);
    }

    // Diagnostics that are useful in support logs but should become visible
    // while running a dev build. Release builds keep the old file-only
    // behavior; dev builds mirror the concise body through the normal console
    // UX so the live prompt tells the operator what is actually happening.
    public static void WriteDiagnostic(LogComponent component, string fileLine, string consoleBody)
    {
        if (DevConsoleDiagnosticsEnabled)
        {
            ConsoleUx.Write(component, consoleBody);
            return;
        }

        Tee(fileLine);
    }

    public static void WarnDiagnostic(LogComponent component, string fileLine, string consoleBody)
    {
        if (DevConsoleDiagnosticsEnabled)
        {
            ConsoleUx.Warn(component, consoleBody);
            return;
        }

        Tee(fileLine);
    }

    private static void Tee(string? line)
    {
        Volatile.Write(ref _lastWriteTicksUtc, DateTime.UtcNow.Ticks);
        var w = _writer;
        if (w == null) return;
        lock (_lock)
        {
            w = _writer;
            if (w == null) return;
            try
            {
                w.Write(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ "));
                w.WriteLine(line ?? "");
                w.Flush();
                if (w.BaseStream.Length >= MaxBytes)
                {
                    OpenNew();
                }
            }
            catch
            {
                // Disk full / file deleted out from under us / etc. The
                // console is the primary UX — log file errors must not
                // surface to the user.
            }
        }
    }

    private static void OpenNew()
    {
        try { _writer?.Dispose(); } catch { /* best-effort */ }
        _writer = null;
        if (_logDir == null) return;
        string ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        string path = Path.Combine(_logDir, $"{_component}-{ts}.log");
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(fs) { AutoFlush = false };
    }

    private static void PruneOld()
    {
        if (_logDir == null) return;
        var cutoff = DateTime.UtcNow - RetentionWindow;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_logDir, $"{_component}-*.log"))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                catch { /* skip individual file */ }
            }
        }
        catch { /* skip */ }
    }

    // Wraps Console.Out so every Console.WriteLine writes to both the
    // original writer and the rolling log file. Only WriteLine(string?)
    // and WriteLine() tee — the per-char Write paths stay console-only
    // (the codebase doesn't use them, and teeing per char would be wasteful).
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _primary;
        public TeeWriter(TextWriter primary) { _primary = primary; }
        public override Encoding Encoding => _primary.Encoding;
        public override void WriteLine() { _primary.WriteLine(); Tee(""); }
        public override void WriteLine(string? value) { _primary.WriteLine(value); Tee(value); }
        public override void Write(string? value) { _primary.Write(value); }
        public override void Write(char value) { _primary.Write(value); }
        public override void Flush() { _primary.Flush(); }
    }
}
