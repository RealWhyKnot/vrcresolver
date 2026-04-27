using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.IPC;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;
// Partial — URL classification and relay-wrap. Pure helpers that decide what to do with a given
// URL: which DNS host owns it, whether AVPro will accept it natively, whether the in-game player
// needs the localhost.youtube.com relay, etc. Plus ApplyRelayWrap, the actual transformation
// from a pristine resolved URL into the wrapped relay URL VRChat fetches.
[SupportedOSPlatform("windows")]
public partial class ResolutionEngine
{
    // --- moved from ResolutionEngine.cs (lines 298-309) ---
    public static string ExtractHost(string url)
    {
        try
        {
            string host = new Uri(url).Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            return host;
        }
        catch { return ""; }
    }

    // IsBotDetectionStderr / BuildBgutilPluginArgs moved to ResolutionEngine.YtDlpProcess.cs.

    // --- moved from ResolutionEngine.cs (lines 352-463) ---
    private bool RequiresNativeAvProUa(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        var denylist = _settings.Config.NativeAvProUaHosts;
        if (denylist == null || denylist.Count == 0) return false;
        foreach (var entry in denylist)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            string d = entry.Trim().ToLowerInvariant();
            if (host == d || host.EndsWith("." + d)) return true;
        }
        return false;
    }

    // VRChat's built-in AVPro trusted-URL allowlist (as of 2026-04-23). Hosts matching these
    // patterns play pristine â€” AVPro accepts them without any trust-list bypass. Hosts OFF this
    // list silently fail with "Loading failed" unless relay-wrapped.
    //
    // Source: in-game trust check shipped with VRChat. Keep synchronized with the
    // project_vrchat_trusted_url_list memory file (same table). Adding an entry is a one-way ticket
    // to skipping the relay wrap on that host, so only add after verifying VRChat trusts it.
    private static readonly string[] _vrchatTrustedHostPatterns = new[]
    {
        "vod-progressive.akamaized.net",
        "*.facebook.com", "*.fbcdn.net",
        "*.googlevideo.com",
        "*.hyperbeam.com", "*.hyperbeam.dev",
        "*.mixcloud.com",
        "*.nicovideo.jp",
        "soundcloud.com", "*.sndcdn.com",
        "*.topaz.chat",
        "*.twitch.tv", "*.ttvnw.net", "*.twitchcdn.net",
        "*.vrcdn.live", "*.vrcdn.video", "*.vrcdn.cloud",
        "*.vimeo.com",
        "*.youku.com",
        "*.youtube.com", "youtu.be",
    };

    public static bool IsVrchatTrustedHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        foreach (var pattern in _vrchatTrustedHostPatterns)
        {
            if (pattern.StartsWith("*."))
            {
                string suffix = pattern.Substring(1); // ".youtube.com"
                string bare = pattern.Substring(2);   // "youtube.com"
                if (host == bare || host.EndsWith(suffix)) return true;
            }
            else
            {
                if (host == pattern) return true;
            }
        }
        return false;
    }

    // Wrap a pristine resolved URL in the localhost.youtube.com relay URL so AVPro sees a trusted
    // host. Default path: wrap everything. Skipped only when:
    //   - skipRelayWrap=true (Share mode â€” user is copying a plain URL out of WKVRCProxy),
    //   - EnableRelayBypass config flag is off,
    //   - the hosts-file mapping isn't active (setup declined),
    //   - the host is in the `NativeAvProUaHosts` config deny-list (movie-world hosts).
    // `forceWrap` is retained for callers (browser-extract session replay) that want to override
    // an otherwise-skipped wrap, but with the default now being "wrap", it's mostly redundant.
    private string ApplyRelayWrap(string pristineUrl, bool skipRelayWrap, string correlationId, bool forceWrap = false)
    {
        if (skipRelayWrap)
            return pristineUrl;
        if (!_settings.Config.EnableRelayBypass)
        {
            _logger.Warning("[" + correlationId + "] Relay bypass is DISABLED in config â€” returning pristine URL. Untrusted hosts will likely fail VRChat's trusted-URL check.");
            return pristineUrl;
        }
        if (!_hostsManager.IsBypassActive())
        {
            _logger.Warning("[" + correlationId + "] Hosts-file bypass is not active â€” returning pristine URL. VRChat's AVPro will reject untrusted hosts. Run the hosts setup from Settings.");
            return pristineUrl;
        }
        if (!forceWrap && RequiresNativeAvProUa(pristineUrl))
        {
            string host = Uri.TryCreate(pristineUrl, UriKind.Absolute, out var u) ? u.Host : "<unparseable>";
            _logger.Info("[" + correlationId + "] Relay wrap skipped for " + host + " â€” host requires AVPro's native UA (NativeAvProUaHosts).");
            return pristineUrl;
        }
        if (!forceWrap && IsVrchatTrustedHost(pristineUrl))
        {
            string host = Uri.TryCreate(pristineUrl, UriKind.Absolute, out var u) ? u.Host : "<unparseable>";
            _logger.Debug("[" + correlationId + "] Relay wrap skipped for " + host + " â€” already on VRChat's trusted-URL list (pristine passthrough).");
            return pristineUrl;
        }
        try
        {
            int port = _relayPortManager.CurrentPort;
            if (port <= 0)
            {
                _logger.Warning("[" + correlationId + "] Relay bypass is enabled but relay port is 0 â€” wrapping skipped. Video will likely fail to play (untrusted host).");
                return pristineUrl;
            }
            string encodedUrl = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pristineUrl));
            string relayUrl = "http://localhost.youtube.com:" + port + "/play?target=" + WebUtility.UrlEncode(encodedUrl);
            _logger.Info("[" + correlationId + "] URL relay-wrapped on port " + port + ".");
            return relayUrl;
        }
        catch (Exception ex)
        {
            _logger.Warning("[" + correlationId + "] Failed to wrap URL for relay: " + ex.Message);
            return pristineUrl;
        }
    }

    // --- moved from ResolutionEngine.cs (lines 1131-1138) ---
    private static string HostFromUrl(string url)
    {
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return ""; }
    }

    // Best-effort reservation of a slot in the rolling window. Returns true if the spawn is
    // allowed and records the timestamp; false if over budget. Old entries are pruned on each call.

    // --- moved from ResolutionEngine.cs (lines 1397-1454) ---
    private static string? ExtractYouTubeVideoId(string url)
    {
        try
        {
            var uri = new Uri(url);
            string path = uri.AbsolutePath;

            // Standard watch URL: youtube.com/watch?v=ID
            if (path == "/watch")
            {
                foreach (string part in uri.Query.TrimStart('?').Split('&'))
                {
                    if (part.StartsWith("v=")) return part.Substring(2);
                }
            }

            // Short URL: youtu.be/ID
            if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
                return path.TrimStart('/').Split('?')[0];

            // Shorts: /shorts/ID
            if (path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
                return path.Substring("/shorts/".Length).Split('/')[0].Split('?')[0];

            // Channel live streams â€” return a stable identifier for the PO token cache key
            // /channel/UCxxx/live  â†’  "channel:UCxxx"
            if (path.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring("/channel/".Length).Split('/')[0];
                if (!string.IsNullOrEmpty(segment)) return "channel:" + segment;
            }

            // /c/Name/live  â†’  "c:Name"
            if (path.StartsWith("/c/", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring("/c/".Length).Split('/')[0];
                if (!string.IsNullOrEmpty(segment)) return "c:" + segment;
            }

            // /user/Name/live  â†’  "user:Name"
            if (path.StartsWith("/user/", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring("/user/".Length).Split('/')[0];
                if (!string.IsNullOrEmpty(segment)) return "user:" + segment;
            }

            // /@handle/live  â†’  "@handle"
            if (path.StartsWith("/@", StringComparison.OrdinalIgnoreCase))
            {
                string segment = path.Substring(1).Split('/')[0]; // keeps the @
                if (!string.IsNullOrEmpty(segment)) return segment;
            }
        }
        catch { }
        return null;
    }

}
