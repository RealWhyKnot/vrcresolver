using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace WKVRCProxy;

// Best-effort, fire-and-forget startup check against the GitHub
// releases-latest endpoint. If a newer version exists, print one line so
// users running the watchdog see the prompt to run WKVRCProxy.Updater.exe.
//
// The dedicated WKVRCProxy.Updater.exe still owns the actual upgrade flow
// (download / SHA-verify / atomic swap / relaunch). This is just the
// "by the way, X is out" line the legacy AppUpdateChecker used to surface
// in the UI; restored here as console output.
//
// Failure modes are silenced — a network outage, GitHub rate-limit, or
// parser drift must never delay the watchdog's startup or scroll a
// stack trace over the operator's banner.
internal static class UpdateCheck
{
    private const string Repo = "RealWhyKnot/WKVRCProxy";
    private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    public static void StartBackgroundCheck()
    {
        _ = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = RequestTimeout };
            var asmVer = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WKVRCProxy-Watchdog/" + asmVer);
            // GitHub returns 403 without an Accept header in some scenarios.
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await http.GetAsync(ApiUrl).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl)
                ? (tagEl.GetString() ?? "")
                : "";
            if (string.IsNullOrEmpty(tag)) return;

            // Tag is like "v2026.5.3.1-A930"; the AssemblyVersion is just
            // the numeric portion. Strip the leading 'v' and any "-XXXX"
            // dev suffix so we compare apples to apples.
            string tagNumeric = tag.TrimStart('v', 'V');
            int dash = tagNumeric.IndexOf('-');
            if (dash >= 0) tagNumeric = tagNumeric[..dash];

            if (!Version.TryParse(tagNumeric, out var remote)) return;
            var local = Assembly.GetEntryAssembly()?.GetName().Version;
            if (local == null) return;
            if (remote <= local) return;

            string url = doc.RootElement.TryGetProperty("html_url", out var urlEl)
                ? (urlEl.GetString() ?? "")
                : "";

            Console.WriteLine(
                "[update] Version " + tag + " available — run WKVRCProxy.Updater.exe to install" +
                (string.IsNullOrEmpty(url) ? "" : " (" + url + ")"));
        }
        catch
        {
            // Network outage / parse drift / rate-limit. Silent on purpose
            // — this is observability, not load-bearing.
        }
    }
}
