using System.Text;
using WKVRCProxy.Shared;

namespace WKVRCProxy.YtDlp;

internal static partial class Program
{
    // Trust-gateway URL wrap (Phase 1, HTTP-only). The watchdog binds a
    // local HTTP listener at 127.0.0.1:{ephemeral} and writes the port to
    // %LOCALAPPDATA%Low\WKVRCProxy\relay_port.txt. We rewrite the resolved
    // URL to `http://localhost.youtube.com:{port}/play/manifest.<ext>?target=<base64>`
    // so VRChat's AVPro allowlist (which has *.youtube.com) accepts it in
    // default-public worlds. The hosts file pins
    // `localhost.youtube.com -> 127.0.0.1` so the request lands on the
    // watchdog's listener which forwards bytes to the real URL.
    //
    // The path component carries a static "manifest" placeholder plus the
    // upstream URL's path extension. AVPro/MediaFoundation dispatches its
    // byte-stream handler primarily on path extension (.m3u8 -> HLS, .mpd
    // -> DASH); without a recognised extension, MF mis-dispatches and
    // playback stalls. The relay ignores the path placeholder when target=
    // is set and resolves the real upstream URL from the base64.
    //
    // Failure modes -- ALL fall through to the raw URL (today's behavior):
    //   - port file missing (watchdog not running OR didn't bind)
    //   - port file unreadable / malformed
    //   - URL is already wrapped (defensive; avoid double-wrap)
    //   - URL is one of AVPro's natively-allowlisted hosts (googlevideo,
    //     vimeo, etc.) where wrapping costs latency for no gain
    private static string TryWrapForTrustGateway(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        // Defensive: don't double-wrap. If the URL is already pointed at
        // localhost.youtube.com it came from somewhere that already did
        // the wrap (or someone manually constructed it).
        if (url.IndexOf("localhost.youtube.com", StringComparison.OrdinalIgnoreCase) >= 0)
            return url;

        int? port = TryReadRelayPort();
        if (!port.HasValue) return url;

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(url))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        string ext = ExtractPathExtension(url);
        string suffix = string.IsNullOrEmpty(ext) ? "" : ("." + ext);
        return "http://localhost.youtube.com:"
            + port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/play/manifest" + suffix + "?target=" + b64;
    }

    // Mirrors LocalRelayServer.ExtractPathExtension. Lowercase, no leading
    // dot. Only emit when the upstream extension is on a small allowlist
    // so unfamiliar suffixes don't end up advertised on the trust-gateway
    // URL where they could mis-dispatch MF onto the wrong handler.
    private static readonly HashSet<string> s_allowedPathExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4s", "m4v", "ts", "m3u8", "mpd",
        "webm", "mkv", "mov",
        "mp3", "m4a", "aac", "ogg", "opus", "wav", "flac",
        "vtt", "srt",
    };

    private static string ExtractPathExtension(string upstreamUrl)
    {
        if (string.IsNullOrEmpty(upstreamUrl)) return "";
        string path;
        try { path = new Uri(upstreamUrl).AbsolutePath; }
        catch { return ""; }
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || ext.Length < 2) return "";
        string trimmed = ext.Substring(1).ToLowerInvariant();
        return s_allowedPathExts.Contains(trimmed) ? trimmed : "";
    }

    private static int? TryReadRelayPort()
    {
        try
        {
            string portFile = Path.Combine(WkvrcPaths.StateRoot(), "relay_port.txt");
            if (!File.Exists(portFile)) return null;
            string text = File.ReadAllText(portFile).Trim();
            if (int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int p)
                && p > 1024 && p < 65536) return p;
        }
        catch { /* best-effort; missing port = no wrap */ }
        return null;
    }
}
