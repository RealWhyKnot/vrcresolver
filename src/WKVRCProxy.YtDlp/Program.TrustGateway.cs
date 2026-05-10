using WKVRCProxy.Shared;

namespace WKVRCProxy.YtDlp;

internal static partial class Program
{
    // Trust-gateway URL wrap (Phase 1, HTTP-only). The watchdog binds a
    // local HTTP listener at 127.0.0.1:{ephemeral} and writes the port to
    // %LOCALAPPDATA%Low\WKVRCProxy\relay_port.txt. We rewrite the resolved
    // URL to `http://localhost.youtube.com:{port}/play/<session>/manifest.<ext>?target=<base64>`
    // so VRChat's AVPro allowlist (which has *.youtube.com) accepts it in
    // default-public worlds. The hosts file pins
    // `localhost.youtube.com -> 127.0.0.1` so the request lands on the
    // watchdog's listener which forwards bytes to the real URL.
    //
    // The path component carries a per-resolve namespace, a static
    // "manifest" placeholder, and the upstream URL's path extension.
    // AVPro/MediaFoundation dispatches its byte-stream handler primarily
    // on path extension (.m3u8 -> HLS, .mpd -> DASH); without a recognised
    // extension, MF mis-dispatches and playback stalls. The namespace lets
    // the relay forward relative playlist subrequests without parsing or
    // rewriting arbitrary manifest bodies.
    //
    // Failure modes -- ALL fall through to the raw URL (today's behavior):
    //   - port file missing (watchdog not running OR didn't bind)
    //   - port file unreadable / malformed
    //   - resolved URL is not a WhyKnot playback proxy URL
    //   - URL is already wrapped (defensive; avoid double-wrap)
    private static string TryWrapForTrustGateway(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        int? port = TryReadRelayPort();
        if (!port.HasValue) return url;

        return TrustGatewayUrlBuilder.TryBuild(port.Value, url, session: null, out string localUrl)
            ? localUrl
            : url;
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
