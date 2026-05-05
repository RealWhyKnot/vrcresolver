using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Runtime updater for the bundled vanilla yt-dlp at
//   <install>/tools/yt-dlp-og-fallback.exe
// The build script fetches the latest yt-dlp at WKVRCProxy build time,
// but yt-dlp upstream pushes extractor patches several times a week
// (especially for YouTube bot-detection). This keeps the shipped fallback
// current between WKVRCProxy releases.
//
// What we DO NOT touch:
//   - <vrcTools>/yt-dlp-og.exe  — VRChat's pinned copy, preserved by
//                                  PatchManager. The patched wrapper exec's
//                                  this on fallback; keeping it pinned
//                                  preserves whatever extractor behaviour
//                                  VRChat shipped with.
//   - <install>/tools/yt-dlp.exe — OUR patched wrapper (R1), versioned
//                                  with WKVRCProxy itself.
//
// Behaviour:
//   - Fire-and-forget at boot.
//   - Once per 24 h via state file at
//     %LOCALAPPDATA%\WKVRCProxy\yt-dlp-update-check.json — won't hit
//     GitHub on a tight relaunch loop.
//   - Compares the bundled fallback's version (read from sibling
//     yt-dlp-og-fallback.version.txt, populated by build.ps1) against
//     yt-dlp/yt-dlp's releases-latest tag.
//   - Downloads asset to a shadow path, atomic-swaps over the live file.
//     The shadow path is a sibling so File.Move is a same-volume rename.
//   - One-line console output per outcome — no stack traces.
[SupportedOSPlatform("windows")]
internal static class YtDlpUpdater
{
    private const string ReleasesUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private const string AssetName = "yt-dlp.exe";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(2);

    public static void StartBackgroundCheck()
    {
        _ = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        try
        {
            string installDir = AppContext.BaseDirectory;
            string toolsDir = Path.Combine(installDir, "tools");
            string fallbackPath = Path.Combine(toolsDir, "yt-dlp-og-fallback.exe");
            string versionFile = Path.Combine(toolsDir, "yt-dlp-og-fallback.version.txt");
            string statePath = StatePath();

            if (!File.Exists(fallbackPath))
            {
                // No bundled fallback to update. Either the install is
                // mid-build or someone wiped tools/. Don't try to repopulate
                // it from here — that's PatchManager's recovery scope.
                return;
            }

            var state = LoadState(statePath);
            if (state.LastCheckUtc.HasValue
                && DateTime.UtcNow - state.LastCheckUtc.Value < CheckInterval)
            {
                return;
            }

            string localVersion = ReadLocalVersion(versionFile);
            string remoteVersion = await FetchRemoteVersionAsync().ConfigureAwait(false);
            state.LastCheckUtc = DateTime.UtcNow;
            state.LastLocal = localVersion;
            state.LastRemote = remoteVersion;

            if (string.IsNullOrEmpty(remoteVersion))
            {
                Console.WriteLine("[yt-dlp] update check failed (could not reach GitHub)");
                SaveState(statePath, state);
                return;
            }

            // yt-dlp uses date-based versions (2026.03.17). Lexical compare
            // is monotonic, but only when both sides parse — bail out gracefully
            // if either is malformed.
            if (!string.IsNullOrEmpty(localVersion)
                && string.CompareOrdinal(localVersion, remoteVersion) >= 0)
            {
                Console.WriteLine("[yt-dlp] bundled fallback up-to-date (" + localVersion + ")");
                SaveState(statePath, state);
                return;
            }

            Console.WriteLine("[yt-dlp] updating bundled fallback "
                + (string.IsNullOrEmpty(localVersion) ? "<unknown>" : localVersion)
                + " → " + remoteVersion);

            bool ok = await DownloadAndSwapAsync(fallbackPath, versionFile, remoteVersion).ConfigureAwait(false);
            if (ok)
            {
                state.LastLocal = remoteVersion;
                Console.WriteLine("[yt-dlp] updated to " + remoteVersion);
            }
            else
            {
                Console.WriteLine("[yt-dlp] update failed — bundled fallback left at " + (localVersion ?? "<unknown>"));
            }
            SaveState(statePath, state);
        }
        catch (Exception ex)
        {
            // Last-ditch — never let a yt-dlp updater failure bubble up
            // to the user as a stack trace. The watchdog is more important.
            Console.WriteLine("[yt-dlp] background error: " + ex.Message);
        }
    }

    private static string ReadLocalVersion(string versionFile)
    {
        try
        {
            if (!File.Exists(versionFile)) return "";
            return File.ReadAllText(versionFile).Trim();
        }
        catch { return ""; }
    }

    private static async Task<string> FetchRemoteVersionAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = HttpTimeout };
            var asmVer = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WKVRCProxy-YtDlpUpdater/" + asmVer);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await http.GetAsync(ReleasesUrl).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return "";
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tag_name", out var tagEl)
                ? (tagEl.GetString() ?? "")
                : "";
        }
        catch { return ""; }
    }

    private static async Task<bool> DownloadAndSwapAsync(string fallbackPath, string versionFile, string tag)
    {
        // Asset URL follows the canonical /releases/download/<tag>/<asset> layout.
        string assetUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/" + tag + "/" + AssetName;
        string shadowPath = fallbackPath + ".new-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using var http = new HttpClient { Timeout = HttpTimeout };
            var asmVer = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WKVRCProxy-YtDlpUpdater/" + asmVer);

            using (var src = await http.GetStreamAsync(assetUrl).ConfigureAwait(false))
            using (var dst = new FileStream(shadowPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(dst).ConfigureAwait(false);
            }

            // Sanity: the downloaded shadow has to actually be an exe of
            // non-trivial size. yt-dlp.exe is normally ~17-19 MiB; refuse
            // anything below 1 MiB as truncated.
            var info = new FileInfo(shadowPath);
            if (info.Length < 1024 * 1024)
            {
                try { File.Delete(shadowPath); } catch { }
                return false;
            }

            // Same-volume atomic rename. PatchManager's recovery path
            // (the only consumer of this file) reads it via File.Copy,
            // never holds an exclusive lock, so the swap is safe even
            // if a tick is mid-flight.
            File.Move(shadowPath, fallbackPath, overwrite: true);

            // Update the sibling version file so future startups skip the
            // re-check via the local-vs-remote fast-path.
            try { File.WriteAllText(versionFile, tag); } catch { /* best-effort */ }
            return true;
        }
        catch
        {
            try { if (File.Exists(shadowPath)) File.Delete(shadowPath); } catch { /* best-effort */ }
            return false;
        }
    }

    private static string StatePath()
    {
        string dir = WkvrcPaths.StateRoot();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "yt-dlp-update-check.json");
    }

    private static UpdateState LoadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return new UpdateState();
            return JsonSerializer.Deserialize(File.ReadAllText(path), MeshJsonContext.Default.UpdateState) ?? new UpdateState();
        }
        catch { return new UpdateState(); }
    }

    private static void SaveState(string path, UpdateState state)
    {
        try
        {
            string tmp = path + ".new";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, MeshJsonContext.Default.UpdateState));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort */ }
    }

    // AOT migration: promoted from private to internal so MeshJsonContext
    // can reference via [JsonSerializable] and emit source-gen formatters.
    internal sealed class UpdateState
    {
        [JsonPropertyName("last_check_utc")] public DateTime? LastCheckUtc { get; set; }
        [JsonPropertyName("last_local")] public string LastLocal { get; set; } = "";
        [JsonPropertyName("last_remote")] public string LastRemote { get; set; } = "";
    }

}
