using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WKVRCProxy.Updater;

// updater.exe — standalone single-file companion to WKVRCProxy.exe.
//
// Run from inside the install folder (or with --install-dir <path>); auto-detects the running
// app's mutex, waits for it to exit, downloads the latest release zip from GitHub, swaps the
// install folder by directory rename, then relaunches WKVRCProxy.
//
// Single-file + self-contained means no external runtime dependency. Console window doubles as
// progress display so we don't pull in WinForms.
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string ReleasesUrl = "https://api.github.com/repos/RealWhyKnot/WKVRCProxy/releases/latest";
    private const string SingleInstanceMutexName = "Local\\WKVRCProxy.UI.SingleInstance";

    static async Task<int> Main(string[] args)
    {
        try
        {
            string installDir = ParseArg(args, "--install-dir") ?? AppDomain.CurrentDomain.BaseDirectory;
            installDir = Path.GetFullPath(installDir.TrimEnd('\\', '/'));
            string logPath = Path.Combine(installDir, "updater.log");

            void Log(string msg)
            {
                string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
                Console.WriteLine(line);
                try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
            }

            Console.Title = "WKVRCProxy Updater";
            Log("Updater started. Install dir: " + installDir);

            if (!File.Exists(Path.Combine(installDir, "WKVRCProxy.exe")))
            {
                Log("ERROR: WKVRCProxy.exe not found in install dir. Refusing to update an unrelated folder.");
                Pause();
                return 2;
            }

            string currentVersion = ReadVersionFile(installDir);
            Log("Current version: " + (string.IsNullOrEmpty(currentVersion) ? "(unknown)" : currentVersion));

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WKVRCProxy-Updater/1.0");
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            Log("Fetching latest release metadata from GitHub...");
            string releaseJson;
            try
            {
                releaseJson = await http.GetStringAsync(ReleasesUrl);
            }
            catch (Exception ex)
            {
                Log("ERROR: Could not reach GitHub: " + ex.Message);
                Pause();
                return 3;
            }

            using var doc = JsonDocument.Parse(releaseJson);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            string body = root.TryGetProperty("body", out var bEl) ? bEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(tag))
            {
                Log("ERROR: Release has no tag.");
                Pause();
                return 4;
            }
            Log("Latest release: " + tag);

            if (!string.IsNullOrEmpty(currentVersion) && CompareVersions(currentVersion, tag) >= 0)
            {
                Log("Already up to date — local " + currentVersion + " >= " + tag + ".");
                Log("Relaunching WKVRCProxy and exiting.");
                Relaunch(installDir, Log);
                return 0;
            }

            // Pick the bundle zip asset.
            string? assetName = null;
            string? assetUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string n = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    if (n.StartsWith("WKVRCProxy-", StringComparison.OrdinalIgnoreCase) &&
                        n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = n;
                        assetUrl = asset.TryGetProperty("browser_download_url", out var dlEl) ? dlEl.GetString() : null;
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(assetUrl))
            {
                Log("ERROR: Release " + tag + " has no WKVRCProxy-*.zip asset. Falling back to releases page.");
                if (root.TryGetProperty("html_url", out var u))
                    Process.Start(new ProcessStartInfo { FileName = u.GetString(), UseShellExecute = true });
                Pause();
                return 5;
            }
            Log("Found asset: " + assetName);

            // Wait for the running app to exit (release file locks). The single-instance mutex is
            // held for the lifetime of WKVRCProxy.exe, so being able to acquire it = app is gone.
            Log("Waiting for WKVRCProxy.exe to exit...");
            if (!await WaitForAppExit(TimeSpan.FromSeconds(30)))
            {
                Log("ERROR: WKVRCProxy is still running after 30 seconds. Close it and re-run the updater.");
                Pause();
                return 6;
            }
            Log("App exited. Proceeding with download.");

            string stagingRoot = Path.Combine(Path.GetTempPath(), "WKVRCProxy-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(stagingRoot);
            string zipPath = Path.Combine(stagingRoot, assetName!);
            string extractDir = Path.Combine(stagingRoot, "extract");

            try
            {
                Log("Downloading " + assetName + "...");
                await DownloadWithProgress(http, assetUrl!, zipPath, Log);

                // Optional SHA256 verification: release notes can contain a "SHA256: <hex>" line.
                string? expectedSha = ExtractSha256(body);
                if (!string.IsNullOrEmpty(expectedSha))
                {
                    Log("Verifying SHA256...");
                    string actual = HashFileSha256(zipPath);
                    if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("ERROR: SHA256 mismatch. Expected " + expectedSha + " got " + actual + ". Aborting.");
                        return 7;
                    }
                    Log("SHA256 OK.");
                }

                Log("Extracting...");
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

                // Some release zips wrap their content in a single top-level dir; flatten.
                string payloadDir = extractDir;
                var topEntries = Directory.GetFileSystemEntries(extractDir);
                if (topEntries.Length == 1 && Directory.Exists(topEntries[0]))
                    payloadDir = topEntries[0];
                if (!File.Exists(Path.Combine(payloadDir, "WKVRCProxy.exe")))
                {
                    Log("ERROR: Extracted payload doesn't contain WKVRCProxy.exe at the expected location.");
                    return 8;
                }

                // Atomic-ish swap. Rename current install dir aside, rename payload into place.
                // %LOCALAPPDATA%\WKVRCProxy\ lives outside the install dir so user data is safe.
                string oldDir = installDir + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                Log("Swapping install dir...");

                bool didMoveOld = false;
                try
                {
                    Directory.Move(installDir, oldDir);
                    didMoveOld = true;
                    Directory.Move(payloadDir, installDir);
                }
                catch (Exception ex)
                {
                    Log("ERROR during swap: " + ex.Message);
                    if (didMoveOld && !Directory.Exists(installDir))
                    {
                        try { Directory.Move(oldDir, installDir); Log("Rolled back to previous install."); }
                        catch (Exception rb) { Log("ROLLBACK FAILED: " + rb.Message + ". Old install preserved at: " + oldDir); }
                    }
                    return 9;
                }

                Log("Swap complete. Updated to " + tag + ".");
                ScheduleDelete(oldDir, Log);

                Relaunch(installDir, Log);
                return 0;
            }
            finally
            {
                try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true); }
                catch { /* leave temp behind if locked */ }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL: " + ex);
            Pause();
            return 1;
        }
    }

    private static string? ParseArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static string ReadVersionFile(string installDir)
    {
        try
        {
            string p = Path.Combine(installDir, "version.txt");
            return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
        }
        catch { return ""; }
    }

    // Mirrors AppUpdateChecker.CompareVersions — duplicated here so the updater stays free of
    // WKVRCProxy.Core dependencies (smaller single-file binary, no Photino transitive pull-in).
    private static int CompareVersions(string local, string remote)
    {
        try
        {
            string a = Strip(local);
            string b = Strip(remote);
            if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
                return va.CompareTo(vb);
        }
        catch { }
        return string.Compare(local, remote, StringComparison.OrdinalIgnoreCase);

        static string Strip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "0.0.0.0";
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            int dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);
            return s;
        }
    }

    private static async Task<bool> WaitForAppExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var m = new Mutex(false, SingleInstanceMutexName);
                if (m.WaitOne(0))
                {
                    try { m.ReleaseMutex(); } catch { }
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous owner crashed. We now own it — release and report.
                return true;
            }
            catch { /* mutex not yet open; keep polling */ }
            await Task.Delay(500);
        }
        return false;
    }

    private static async Task DownloadWithProgress(HttpClient http, string url, string destPath, Action<string> log)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(destPath);

        var buf = new byte[81920];
        long received = 0;
        int lastPct = -1;
        int read;
        while ((read = await src.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            await dst.WriteAsync(buf, 0, read);
            received += read;
            if (total.HasValue)
            {
                int pct = (int)(received * 100 / total.Value);
                if (pct >= lastPct + 5 && pct <= 100)
                {
                    log("  " + pct + "% (" + (received / 1024 / 1024) + " / " + (total.Value / 1024 / 1024) + " MB)");
                    lastPct = pct;
                }
            }
        }
        log("Download complete (" + (received / 1024 / 1024) + " MB).");
    }

    private static string? ExtractSha256(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        foreach (var line in body.Split('\n'))
        {
            int idx = line.IndexOf("SHA256:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string rest = line.Substring(idx + 7).Trim();
                // Strip wrappers like backticks if present.
                rest = rest.Trim('`', ' ', '\r');
                int space = rest.IndexOfAny(new[] { ' ', '\t' });
                if (space > 0) rest = rest.Substring(0, space);
                if (rest.Length == 64 && IsHex(rest)) return rest;
            }
        }
        return null;

        static bool IsHex(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }
    }

    private static string HashFileSha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        byte[] hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash);
    }

    private static void ScheduleDelete(string path, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c timeout /t 3 >nul & rmdir /s /q \"" + path + "\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            log("Scheduled cleanup of previous install: " + path);
        }
        catch (Exception ex) { log("WARN: Could not schedule cleanup of " + path + ": " + ex.Message); }
    }

    private static void Relaunch(string installDir, Action<string> log)
    {
        try
        {
            string exe = Path.Combine(installDir, "WKVRCProxy.exe");
            Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = installDir, UseShellExecute = true });
            log("Relaunched WKVRCProxy.");
        }
        catch (Exception ex) { log("WARN: Could not relaunch WKVRCProxy: " + ex.Message); }
    }

    private static void Pause()
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to close.");
            Console.ReadKey(true);
        }
        catch { /* non-interactive */ }
    }
}
