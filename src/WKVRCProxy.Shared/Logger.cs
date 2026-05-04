using System.Text;

namespace WKVRCProxy.Shared;

// Tees Console.Out to a rolling file at
//   %LOCALAPPDATA%\WKVRCProxy\logs\<component>-<utc>.log
// Console stays the primary UX; the file is the support artifact users
// can attach to bug reports without scraping their scrollback.
//
// Rotation: a fresh file is opened on Install() (per-process), and again
// whenever the active file passes 10 MiB. Retention: any <component>-*.log
// older than 7 days (mtime) is deleted on Install().
//
// Each exe (watchdog, updater, uninstaller) calls Install at the top of
// Main so its boot output is captured.
public static class Logger
{
    private const long MaxBytes = 10L * 1024 * 1024;
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);

    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static string? _logDir;
    private static string _component = "unknown";
    private static int _installed;

    public static void Install(string component)
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        _component = component;
        try
        {
            _logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WKVRCProxy", "logs");
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

    private static void Tee(string? line)
    {
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
