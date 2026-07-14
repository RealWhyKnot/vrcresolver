namespace VrcResolver.Shared;

public static class FirstPartyUrlPolicy
{
    private static readonly HashSet<string> s_allowedPathExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4s", "m4v", "ts", "m3u8", "mpd",
        "webm", "mkv", "mov",
        "mp3", "m4a", "aac", "ogg", "opus", "wav", "flac",
        "vtt", "srt",
    };

    // Both host families are first-party: the server intentionally returns
    // whyknot-family playback URLs for wire compatibility with pre-rename
    // clients, so this client must recognize both as its own.
    public static bool IsFirstPartyHost(string host)
    {
        return host.Equals("vrcresolver.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".vrcresolver.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("whyknot.dev", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".whyknot.dev", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFirstPartyPlaybackProxyUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return IsFirstPartyPlaybackProxyUri(uri);
    }

    public static bool IsFirstPartyPlaybackProxyUri(Uri uri)
    {
        return IsFirstPartyHost(uri.Host) && IsPlaybackProxyPath(uri.AbsolutePath);
    }

    public static string ExtractAllowedPathExtension(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
            return ExtractAllowedPathExtensionFromPath(uri.AbsolutePath);
        }
        catch
        {
            return "";
        }
    }

    public static string ExtractAllowedPathExtensionFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || ext.Length < 2) return "";
        string trimmed = ext.Substring(1).ToLowerInvariant();
        return s_allowedPathExts.Contains(trimmed) ? trimmed : "";
    }

    public static string PlaybackProxyExtensionForTrustGateway(string url)
    {
        string ext = ExtractAllowedPathExtension(url);
        if (!string.IsNullOrEmpty(ext)) return ext;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsFirstPartyPlaybackProxyUri(uri))
        {
            return "";
        }

        string path = uri.AbsolutePath;
        if (path.Equals("/api/proxy", StringComparison.OrdinalIgnoreCase)
            && uri.Query.Contains("q=", StringComparison.OrdinalIgnoreCase))
        {
            return "m3u8";
        }

        if (path.Contains("manifest", StringComparison.OrdinalIgnoreCase))
            return "m3u8";

        return "bin";
    }

    private static bool IsPlaybackProxyPath(string path)
    {
        return path.Equals("/api/proxy", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/proxy/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/popcorn/proxy", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/popcorn/proxy/", StringComparison.OrdinalIgnoreCase);
    }
}
