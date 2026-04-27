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
            bool fromTemp = HasFlag(args, "--from-temp");
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

            // Self-relaunch from %TEMP% if we're running from inside the install dir. The atomic
            // swap below renames the install dir aside (Directory.Move) — Windows refuses if any
            // file in that dir is currently executing, so an updater.exe living *inside* the dir
            // it's about to swap fails with ERROR_SHARING_VIOLATION. Symptom: the user sees the
            // console flash and close, version.txt never updates, and the "Update available"
            // banner reappears on next launch. Copying ourselves to %TEMP% and re-execing breaks
            // the lock without changing anything else.
            string ownExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            string ownDir = Path.GetDirectoryName(ownExe) ?? "";
            bool runningFromInstallDir = !string.IsNullOrEmpty(ownDir)
                && string.Equals(Path.GetFullPath(ownDir).TrimEnd('\\', '/'),
                                 installDir, StringComparison.OrdinalIgnoreCase);
            if (runningFromInstallDir && !fromTemp)
            {
                string tempCopy = Path.Combine(Path.GetTempPath(),
                    "WKVRCProxy-updater-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                Log("Running from inside install dir; relaunching from " + tempCopy + " to release file lock.");
                try { File.Copy(ownExe, tempCopy, overwrite: true); }
                catch (Exception cex)
                {
                    Log("ERROR: Could not copy self to temp: " + cex.Message);
                    Pause();
                    return 10;
                }
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tempCopy,
                        Arguments = "--install-dir \"" + installDir + "\" --from-temp",
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetTempPath()
                    });
                }
                catch (Exception sex)
                {
                    Log("ERROR: Could not exec temp copy: " + sex.Message);
                    Pause();
                    return 11;
                }
                // Original process exits cleanly so the install-dir file lock drops.
                return 0;
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
            string assetUrlFinal = assetUrl!;
            string assetNameFinal = assetName!;
            Log("Found asset: " + assetName);

            // Mandatory SHA256 gate. The release workflow (.github/workflows/release.yml) always
            // emits a "SHA256: <hex>" line in the release notes. If it's missing or malformed we
            // refuse to proceed — we will not download an unverifiable zip and swap it in over
            // the user's install. Fail fast (before WaitForAppExit + download) so the user isn't
            // forced to close VRChat just to see the error.
            string? expectedSha = ExtractSha256(body);
            if (string.IsNullOrEmpty(expectedSha))
            {
                Log("ERROR: Release " + tag + " has no SHA256 line in its notes (or the value is malformed).");
                Log("       Refusing to install an unverified update. Re-download manually from the releases page if needed:");
                if (root.TryGetProperty("html_url", out var hu0))
                    Log("       " + (hu0.GetString() ?? "https://github.com/RealWhyKnot/WKVRCProxy/releases/latest"));
                else
                    Log("       https://github.com/RealWhyKnot/WKVRCProxy/releases/latest");
                Pause();
                return 12;
            }
            Log("Release notes report SHA256: " + expectedSha);

            // Wait for the running app to exit (release file locks). The single-instance mutex is
            // held for the lifetime of WKVRCProxy.exe, so being able to acquire it = app is gone.
            // When we self-relaunched from %TEMP%, the original updater (still inside the install
            // dir) just exited, but Windows takes a moment to fully release the .exe handle —
            // give the wait an extra few seconds in that case.
            Log("Waiting for WKVRCProxy.exe to exit...");
            var waitTimeout = fromTemp ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(30);
            if (!await WaitForAppExit(waitTimeout))
            {
                Log("ERROR: WKVRCProxy is still running after " + waitTimeout.TotalSeconds + " seconds. Close it and re-run the updater.");
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
                Log("Downloading " + assetNameFinal + "...");
                await DownloadWithProgress(http, assetUrlFinal, zipPath, Log);

                // Mandatory SHA256 verification. The expected hash was already parsed (and required
                // to be present) before WaitForAppExit. Mismatch is a hard fail — we never extract
                // a zip whose contents don't match the release-notes-attested hash.
                Log("Verifying SHA256...");
                string actualSha = HashFileSha256(zipPath);
                if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    Log("ERROR: SHA256 mismatch. Expected " + expectedSha + " got " + actualSha + ". Aborting.");
                    Pause();
                    return 7;
                }
                Log("SHA256 OK.");

                Log("Extracting...");
                Directory.CreateDirectory(extractDir);
                // Zip Slip guard: every entry's resolved destination must stay under extractDir.
                // ZipFile.ExtractToDirectory has its own check on modern .NET, but we extract
                // entry-by-entry so we can refuse the whole update on the first escape attempt
                // rather than rely on framework-version-specific behaviour.
                if (!TryExtractZipSafely(zipPath, extractDir, Log, out string? extractError))
                {
                    Log("ERROR: " + (extractError ?? "Zip extraction rejected.") + " Aborting.");
                    Pause();
                    return 13;
                }

                // Some release zips wrap their content in a single top-level dir; flatten.
                string payloadDir = extractDir;
                var topEntries = Directory.GetFileSystemEntries(extractDir);
                if (topEntries.Length == 1 && Directory.Exists(topEntries[0]))
                    payloadDir = topEntries[0];
                if (!File.Exists(Path.Combine(payloadDir, "WKVRCProxy.exe")))
                {
                    Log("ERROR: Extracted payload doesn't contain WKVRCProxy.exe at the expected location.");
                    Pause();
                    return 8;
                }

                // Atomic-ish swap. Rename current install dir aside, rename payload into place.
                // %LOCALAPPDATA%\WKVRCProxy\ lives outside the install dir so user data is safe.
                string oldDir = installDir + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                Log("Swapping install dir...");

                string? releasePageUrl = null;
                if (root.TryGetProperty("html_url", out var hu)) releasePageUrl = hu.GetString();

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
                        try { Directory.Move(oldDir, installDir); Log("Rolled back to previous install."); Pause(); }
                        catch (Exception rb)
                        {
                            // Catastrophic: the install dir is gone AND the rollback couldn't put
                            // it back. The user's WKVRCProxy is effectively uninstalled. Show the
                            // big scary banner with concrete recovery steps so the user doesn't
                            // assume the update succeeded when it has actually wiped their app.
                            ShowCatastrophicFailureScreen(oldDir, releasePageUrl, rb.Message, Log);
                        }
                    }
                    else
                    {
                        Pause();
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

    private static bool HasFlag(string[] args, string name)
    {
        foreach (var a in args)
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The "everything went wrong" screen. Reached when the install dir was renamed aside but the
    // new payload couldn't be moved into place AND the rollback also failed — the user is left
    // with no install at the original path, just a backup folder. Big banner, plain language,
    // explicit recovery steps. Opens the GitHub releases page in the default browser so the
    // re-download is a single click.
    private static void ShowCatastrophicFailureScreen(string backupDir, string? releaseUrl, string rollbackError, Action<string> log)
    {
        string bar = new string('=', 78);
        Console.WriteLine();
        Console.WriteLine(bar);
        Console.WriteLine(" SOMETHING WENT VERY WRONG");
        Console.WriteLine(bar);
        Console.WriteLine();
        Console.WriteLine(" The automatic update couldn't finish AND the rollback to your previous");
        Console.WriteLine(" install didn't work either. WKVRCProxy isn't installed at the original");
        Console.WriteLine(" location right now.");
        Console.WriteLine();
        Console.WriteLine(" Your previous install is preserved at:");
        Console.WriteLine("   " + backupDir);
        Console.WriteLine();
        Console.WriteLine(" Recovery — please go download the latest release manually:");
        if (!string.IsNullOrEmpty(releaseUrl))
            Console.WriteLine("   " + releaseUrl);
        else
            Console.WriteLine("   https://github.com/RealWhyKnot/WKVRCProxy/releases/latest");
        Console.WriteLine();
        Console.WriteLine(" Extract the new zip wherever you want WKVRCProxy to live, then copy");
        Console.WriteLine(" your old data files (app_config.json, strategy_memory.json, etc.) from");
        Console.WriteLine(" the backup folder above into the fresh install if you want to keep");
        Console.WriteLine(" your settings.");
        Console.WriteLine();
        Console.WriteLine(" Rollback error (for the report): " + rollbackError);
        Console.WriteLine(bar);
        Console.WriteLine();

        log("CATASTROPHIC: install dir gone, rollback failed. Backup at " + backupDir);

        // Best-effort: open the releases page in the user's browser. If the user has no default
        // browser configured, this silently fails — the URL is still printed above.
        if (!string.IsNullOrEmpty(releaseUrl))
        {
            try { Process.Start(new ProcessStartInfo { FileName = releaseUrl, UseShellExecute = true }); }
            catch { }
        }
        Pause();
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

    // Zip Slip-safe extraction. For each entry, resolve the destination via Path.GetFullPath and
    // require it to stay under destinationDir. A single escaping entry rejects the whole update —
    // we will not extract a partial payload over the user's install. Returns false on the first
    // bad entry, with `error` describing what was rejected; on success returns true and `error` is
    // null. Mirrors the canonical fix from
    //   https://learn.microsoft.com/dotnet/api/system.io.compression.zipfileextensions.extracttodirectory
    // but we own the loop so the rejection is visible at this layer rather than tucked inside
    // framework version-specific behaviour (older .NET runtimes did not validate).
    private static bool TryExtractZipSafely(string zipPath, string destinationDir, Action<string> log, out string? error)
    {
        error = null;
        // Canonicalise the destination so the prefix check below sees the same form
        // GetFullPath produces for entry destinations.
        string canonicalDest = Path.GetFullPath(destinationDir);
        string destWithSep = canonicalDest.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? canonicalDest
            : canonicalDest + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            // Empty FullName means a malformed entry; skip rather than treat as escape.
            if (string.IsNullOrEmpty(entry.FullName)) continue;

            // Resolve where the entry would land. Path.GetFullPath collapses .., absolute roots,
            // and alt-separators, which is exactly what a Zip Slip attack tries to abuse.
            string targetPath = Path.GetFullPath(Path.Combine(canonicalDest, entry.FullName));

            bool isDirEntry = entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\");

            // The entry must either be the destination itself (rare — a self-reference) or a path
            // strictly under destination + separator. Anything else escapes.
            if (!string.Equals(targetPath, canonicalDest, StringComparison.OrdinalIgnoreCase)
                && !targetPath.StartsWith(destWithSep, StringComparison.OrdinalIgnoreCase))
            {
                error = "Zip entry '" + entry.FullName + "' resolves outside the staging directory ('" + targetPath + "'). Refusing the whole update.";
                log(error);
                return false;
            }

            if (isDirEntry)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            // Make sure the parent dir exists. CreateDirectory is a no-op if it already does.
            string? parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            entry.ExtractToFile(targetPath, overwrite: true);
        }
        return true;
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
