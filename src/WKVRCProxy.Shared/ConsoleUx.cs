using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WKVRCProxy.Shared;

// Centralized formatting + colour for user-facing console lines.
// All output routes through a single static lock so concurrent writers
// (resolves, mesh reconnects, hosts ticks, etc.) cannot interleave their
// colour-state changes mid-line. Falls back to plain text when stdout is
// not a TTY (CI capture, Get-Content tail, etc.).

public enum LogComponent
{
    Mesh,
    Ipc,
    Hosts,
    Patch,
    Wrapper,
    Heartbeat,
    Relay,
    Shutdown,
}

public enum ResolveStatus
{
    Resolved,
    Cached,
    Fallback,
    Failed,
    Unexpected,
}

public static class ConsoleUx
{
    private static readonly object s_lock = new();

    // Component palette. Picked for visual contrast across the default
    // Windows console + most terminals: each component lands on a
    // distinguishable hue from its neighbours so a sysadmin scanning the
    // log can colour-filter by source. Warnings always render yellow,
    // overriding the component colour, so a [warn] line stands out
    // regardless of which component emitted it.
    private static ConsoleColor ColorFor(LogComponent c) => c switch
    {
        LogComponent.Mesh      => ConsoleColor.Cyan,
        LogComponent.Ipc       => ConsoleColor.Magenta,
        LogComponent.Hosts     => ConsoleColor.Blue,
        LogComponent.Patch     => ConsoleColor.DarkYellow,
        LogComponent.Wrapper   => ConsoleColor.DarkMagenta,
        LogComponent.Heartbeat => ConsoleColor.DarkGray,
        LogComponent.Relay     => ConsoleColor.DarkCyan,
        LogComponent.Shutdown  => ConsoleColor.DarkRed,
        _                      => ConsoleColor.Gray,
    };

    private static string Tag(LogComponent c) => c switch
    {
        LogComponent.Mesh      => "[mesh]",
        LogComponent.Ipc       => "[ipc]",
        LogComponent.Hosts     => "[hosts]",
        LogComponent.Patch     => "[patch]",
        LogComponent.Wrapper   => "[wrapper]",
        LogComponent.Heartbeat => "[heartbeat]",
        LogComponent.Relay     => "[relay]",
        LogComponent.Shutdown  => "[shutdown]",
        _                      => "[?]",
    };

    public static void Write(LogComponent c, string body)
    {
        WriteStamped(ColorFor(c), $"{Tag(c)} {body}");
    }

    public static void Warn(LogComponent c, string body)
    {
        WriteStamped(ConsoleColor.Yellow, $"{Tag(c)}[warn] {body}");
    }

    // Component-tagged error. Same shape as Warn but red. Use for paths that
    // are "we cannot proceed but the process keeps running" (an [err] sub-tag
    // in the existing convention). For terminal halts use Fatal instead.
    public static void Error(LogComponent c, string body)
    {
        WriteStamped(ConsoleColor.Red, $"{Tag(c)}[err] {body}");
    }

    public static void Fatal(string body)
    {
        WriteStamped(ConsoleColor.Red, $"[FATAL] {body}");
    }

    // Per-resolve outcome line. Fixed-column layout so the operator can
    // eyeball columns down a busy log without parsing. Status token at
    // the front (OK/!!/XX/??) carries the colour; the same status word
    // follows in plain text so an operator reading a scrub-stripped log
    // file (no colour) still understands the outcome.
    //
    //   [HH:mm:ss] OK   resolved   youtube.com                  AVPro 1080p     2.5s
    //   [HH:mm:ss] OK   cached     youtu.be                     AVPro 1080p     0.0s
    //   [HH:mm:ss] !!   fallback   vimeo.com                    AVPro 720p      8.1s   anti_bot
    //   [HH:mm:ss] XX   failed     wacom.b-cdn.net              AVPro 720p     12.3s   timeout
    //
    // The lh-yt routing marker, when present, replaces the leading two
    // spaces after the host column so wider hosts still align downstream.
    private const int HostWidth   = 28;
    private const int PlayerWidth = 14;
    private const int OutcomeWidth = 9;

    public static void ResolveOutcome(
        string host,
        string player,
        ResolveStatus status,
        bool viaCache,
        bool viaLhYt,
        TimeSpan elapsed,
        string? reason)
    {
        ConsoleColor color;
        string token;
        string word;
        switch (status)
        {
            case ResolveStatus.Resolved:
                color = ConsoleColor.Green;
                token = "OK";
                word  = viaCache ? "cached" : "resolved";
                break;
            case ResolveStatus.Cached:
                color = ConsoleColor.Green;
                token = "OK";
                word  = "cached";
                break;
            case ResolveStatus.Fallback:
                color = ConsoleColor.Yellow;
                token = "!!";
                word  = "fallback";
                break;
            case ResolveStatus.Failed:
                color = ConsoleColor.Red;
                token = "XX";
                word  = "failed";
                break;
            default:
                color = ConsoleColor.DarkGray;
                token = "??";
                word  = "unknown";
                break;
        }

        string hostCol   = TruncatePad(host,   HostWidth);
        string playerCol = TruncatePad(player, PlayerWidth);
        string ytTag     = viaLhYt ? " [yt]" : "     ";
        string elapsedStr = FormatElapsed(elapsed);
        string reasonSuffix = string.IsNullOrEmpty(reason) ? "" : "   " + reason;

        string line = string.Format(
            CultureInfo.InvariantCulture,
            " {0}  {1,-" + OutcomeWidth + "} {2}{3}  {4}  {5,6}{6}",
            token, word, hostCol, ytTag, playerCol, elapsedStr, reasonSuffix);

        WriteStamped(color, line);
    }

    // Wrapper og-fallback notification line. Lives next to the resolve
    // outcomes visually but is emitted by the wrapper out-of-band, not
    // by the resolve dispatch.
    public static void WrapperFallback(string host, string reason, long elapsedMs)
    {
        string line = string.Format(
            CultureInfo.InvariantCulture,
            " !!  {0,-" + OutcomeWidth + "} {1}       {2}   elapsed={3}ms",
            "wrapper", TruncatePad(host, HostWidth), reason, elapsedMs);
        WriteStamped(ConsoleColor.Yellow, line);
    }

    // Multi-line startup banner. Boxed for visual separation from the
    // running log below; key-value paths in a two-column block under the
    // box. Box uses ASCII '=' / '-' so it renders identically across
    // every terminal, code page, and log-tail tool.
    public static void Banner(
        string version,
        string sha,
        string buildTime,
        bool isDev,
        IReadOnlyList<(string Label, string Value)> paths)
    {
        const string divider    = "============================================================";
        const string subdivider = "------------------------------------------------------------";

        lock (s_lock)
        {
            WriteRaw(ConsoleColor.Cyan, divider);
            WriteRaw(ConsoleColor.White, $"  WKVRCProxy {version}");
            WriteRaw(ConsoleColor.Gray,  $"  sha {sha}  built {buildTime}");
            if (isDev)
            {
                WriteRaw(ConsoleColor.Yellow, "  mode: DEV (verbose [relay] req= trace enabled)");
            }
            WriteRaw(ConsoleColor.Cyan, subdivider);

            int labelWidth = paths.Max(p => p.Label.Length);
            foreach (var (label, value) in paths)
            {
                string padded = (label + ":").PadRight(labelWidth + 2);
                WriteRaw(ConsoleColor.Gray, $"  {padded} {value}");
            }
            WriteRaw(ConsoleColor.Cyan, divider);
            WriteRaw(ConsoleColor.Gray, "");
        }
    }

    // Visual phase divider for transitions inside a session (startup
    // complete, mesh up, shutting down, etc.). Single horizontal rule
    // with a short label inline.
    public static void PhaseDivider(string label)
    {
        const int width = 60;
        string body = " " + label + " ";
        int pad = Math.Max(0, width - body.Length);
        int left = pad / 2;
        int right = pad - left;
        string line = new string('-', left) + body + new string('-', right);
        WriteStamped(ConsoleColor.DarkGray, line);
    }

    private static string TruncatePad(string s, int width)
    {
        if (s == null) s = "";
        if (s.Length > width)
        {
            // Keep as much of the head as fits, two-char ellipsis so the
            // operator sees the truncation. Width must be >= 4 for ".." to
            // make sense; smaller widths just truncate.
            return width >= 4 ? s.Substring(0, width - 2) + ".." : s.Substring(0, width);
        }
        return s.PadRight(width);
    }

    private static string FormatElapsed(TimeSpan t)
    {
        double seconds = t.TotalSeconds;
        if (seconds < 60.0)
        {
            return seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }
        int m = (int)(seconds / 60.0);
        int s = (int)(seconds - m * 60.0);
        return m + "m" + s + "s";
    }

    private static void WriteStamped(ConsoleColor color, string body)
    {
        string stamped = "[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + body;
        WriteRaw(color, stamped);
    }

    private static void WriteRaw(ConsoleColor color, string text)
    {
        lock (s_lock)
        {
            ConsoleColor prev;
            try { prev = Console.ForegroundColor; }
            catch { prev = ConsoleColor.Gray; }
            try
            {
                try { Console.ForegroundColor = color; } catch { /* no-tty */ }
                Console.WriteLine(text);
            }
            finally
            {
                try { Console.ForegroundColor = prev; } catch { /* no-tty */ }
            }
        }
    }
}
