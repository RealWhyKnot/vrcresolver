namespace WKVRCProxy;

internal static class TerminalCapabilities
{
    public static bool UseColor()
    {
        if (Console.IsOutputRedirected) return false;
        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    }

    public static bool UseAnimations()
    {
        if (!Environment.UserInteractive) return false;
        if (Console.IsOutputRedirected) return false;
        if (Console.IsInputRedirected) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return false;
        string? disabled = Environment.GetEnvironmentVariable("WKVRCPROXY_NO_ANIMATIONS");
        return !string.Equals(disabled, "1", StringComparison.Ordinal)
            && !string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool UseUnicode()
    {
        if (Console.IsOutputRedirected) return false;
        string? ascii = Environment.GetEnvironmentVariable("WKVRCPROXY_ASCII_TERMINAL");
        if (string.Equals(ascii, "1", StringComparison.Ordinal)
            || string.Equals(ascii, "true", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            string webName = Console.OutputEncoding.WebName;
            return webName.Contains("utf", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    public static bool TrySetCursorVisible(bool visible, out bool previous)
    {
        previous = true;
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            previous = Console.CursorVisible;
            Console.CursorVisible = visible;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void RestoreCursorVisible(bool visible)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try { Console.CursorVisible = visible; }
        catch { /* no cursor */ }
    }
}
