using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace WKVRCProxy;

// HLS manifest rewriter. Every segment URL in the manifest gets routed
// through the local relay so AVPro sees a localhost.youtube.com URL while
// bytes still flow through whyknot.dev.
[SupportedOSPlatform("windows")]
internal static partial class HlsManifestRewriter
{
    [GeneratedRegex(@"URI=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex UriAttributeRegex();

    public static string Rewrite(string manifest, string baseUrl, int port, SegmentIdRegistry registry)
    {
        if (string.IsNullOrEmpty(manifest)) return manifest;

        Uri? baseUri = null;
        Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri);
        string portStr = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var sb = new StringBuilder(manifest.Length + 256);
        foreach (string rawLine in manifest.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (line.Length == 0)
            {
                sb.Append('\n');
                continue;
            }

            if (line.StartsWith('#'))
            {
                string rewrittenTag = UriAttributeRegex().Replace(line, m =>
                {
                    string resolved = ResolveAgainstBase(baseUri, m.Groups[1].Value);
                    string emitted = WrapSegmentThroughListener(resolved, portStr, registry);
                    return "URI=\"" + emitted + "\"";
                });
                sb.Append(rewrittenTag);
                sb.Append('\n');
                continue;
            }

            string trimmed = line.Trim();
            string segResolved = ResolveAgainstBase(baseUri, trimmed);
            if (Uri.TryCreate(segResolved, UriKind.Absolute, out _))
            {
                sb.Append(WrapSegmentThroughListener(segResolved, portStr, registry));
                sb.Append('\n');
            }
            else
            {
                sb.Append(line);
                sb.Append('\n');
            }
        }

        if (sb.Length > 0 && sb[^1] == '\n') sb.Length -= 1;
        return sb.ToString();
    }

    private static string ResolveAgainstBase(Uri? baseUri, string maybeRelative)
    {
        if (Uri.TryCreate(maybeRelative, UriKind.Absolute, out _))
            return maybeRelative;
        if (baseUri == null) return maybeRelative;
        if (Uri.TryCreate(baseUri, maybeRelative, out var resolved))
            return resolved.ToString();
        return maybeRelative;
    }

    private static readonly HashSet<string> s_allowedPathExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4s", "m4v", "ts", "m3u8", "mpd",
        "webm", "mkv", "mov",
        "mp3", "m4a", "aac", "ogg", "opus", "wav", "flac",
        "vtt", "srt",
    };

    internal static string ExtractPathExtension(string upstreamUrl)
    {
        if (string.IsNullOrEmpty(upstreamUrl)) return "";
        string path;
        try { path = new Uri(upstreamUrl).AbsolutePath; }
        catch { return ""; }
        int lastSlash = path.LastIndexOf('/');
        string lastSegment = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
        if (string.IsNullOrEmpty(lastSegment)) return "";
        int dotIdx = lastSegment.LastIndexOf('.');
        string ext = dotIdx >= 0
            ? lastSegment.Substring(dotIdx + 1)
            : lastSegment;
        ext = ext.ToLowerInvariant();
        return s_allowedPathExts.Contains(ext) ? ext : "";
    }

    internal static string WrapSegmentThroughListener(string resolvedSegmentUrl, string portStr, SegmentIdRegistry registry)
    {
        string ext = ExtractPathExtension(resolvedSegmentUrl);
        string id = registry.GetOrAddId(resolvedSegmentUrl, ext);
        string suffix = string.IsNullOrEmpty(ext) ? "" : ("." + ext);
        return "http://localhost.youtube.com:" + portStr + "/play/" + id + suffix;
    }
}
