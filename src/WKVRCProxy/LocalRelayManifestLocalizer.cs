using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

[SupportedOSPlatform("windows")]
internal static partial class LocalRelayManifestLocalizer
{
    public const int MaxManifestBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> s_manifestExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "m3u8",
        "mpd",
    };

    public static bool IsLikelyManifest(string localPath, string targetUrl, MediaTypeHeaderValue? contentType)
    {
        if (HasManifestExtension(localPath)) return true;
        if (LooksLikeManifestPath(localPath)) return true;
        if (TryGetUriPath(targetUrl, out string targetPath)
            && (HasManifestExtension(targetPath) || LooksLikeManifestPath(targetPath)))
        {
            return true;
        }

        string mediaType = contentType?.MediaType ?? "";
        return mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("x-mpegurl", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanBuffer(long? contentLength)
    {
        return !contentLength.HasValue || contentLength.Value <= MaxManifestBytes;
    }

    public static string Localize(string content, string localPath)
    {
        if (string.IsNullOrEmpty(content)) return content;

        var output = new StringBuilder(content.Length + 256);
        using var reader = new StringReader(content);
        string? line;
        bool wrote = false;

        while ((line = reader.ReadLine()) != null)
        {
            if (wrote) output.Append('\n');
            wrote = true;

            string localized = RewriteAbsoluteWhyKnotProxyUrls(line);
            if (!localized.StartsWith("#", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(localized))
            {
                string trimmed = localized.Trim();
                if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    && TryBuildLocalRelativeTarget(trimmed, out string replacement))
                {
                    int start = localized.IndexOf(trimmed, StringComparison.Ordinal);
                    localized = localized.Substring(0, start)
                        + replacement
                        + localized.Substring(start + trimmed.Length);
                }
            }

            output.Append(localized);
        }

        return output.ToString();
    }

    private static string RewriteAbsoluteWhyKnotProxyUrls(string line)
    {
        return AbsoluteUrlRegex().Replace(line, match =>
        {
            string original = WebUtility.HtmlDecode(match.Value.Trim());
            if (!TryBuildLocalRelativeTarget(original, out string replacement))
                return match.Value;

            return replacement;
        });
    }

    private static bool TryBuildLocalRelativeTarget(string original, out string replacement)
    {
        replacement = "";
        if (!Uri.TryCreate(original, UriKind.Absolute, out var uri)) return false;
        if (!WhyKnotUrlPolicy.IsWhyKnotPlaybackProxyUri(uri)) return false;

        string ext = WhyKnotUrlPolicy.ExtractAllowedPathExtensionFromPath(uri.AbsolutePath);
        if (string.IsNullOrEmpty(ext)) ext = "bin";
        string name = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrEmpty(name) || !name.Contains('.', StringComparison.Ordinal))
            name = "proxy." + ext;

        string normalized = uri.ToString();
        string encoded = LocalRelayTargetResolver.EncodeTargetParam(normalized);
        replacement = "proxy/" + StableTargetNamespace(normalized) + "/" + name + "?target=" + encoded;

        return true;
    }

    private static string StableTargetNamespace(string targetUrl)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(targetUrl));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static bool HasManifestExtension(string path)
    {
        string ext = WhyKnotUrlPolicy.ExtractAllowedPathExtensionFromPath(path);
        return s_manifestExts.Contains(ext);
    }

    private static bool LooksLikeManifestPath(string path)
    {
        string fileName = Path.GetFileName(path);
        return fileName.StartsWith("manifest.", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("manifest", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/manifest.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetUriPath(string url, out string path)
    {
        path = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        path = uri.AbsolutePath;
        return true;
    }

    [GeneratedRegex("https?://[^\\s\"'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrlRegex();
}
