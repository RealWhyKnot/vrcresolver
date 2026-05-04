using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WKVRCProxy.Shared;

namespace WKVRCProxy.Updater;

// No flags. Running this exe IS the request to check-and-maybe-update.
//   1. Read current version from the WKVRCProxy.exe sitting next to us.
//   2. Hit GitHub's releases-latest API for RealWhyKnot/WKVRCProxy.
//   3. If newer, prompt with a 15s timeout (default No on timeout).
//   4. On Yes: download zip, verify SHA256 from release body, extract,
//      stop the running watchdog, swap files atomically, relaunch, exit.
//
// Failure-mode invariant: the watchdog is only stopped once the new
// payload has been downloaded AND SHA-verified AND extracted. Any
// failure before that step leaves the running watchdog untouched.
internal static class Program
{
    private const string Repo = "RealWhyKnot/WKVRCProxy";
    private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string WatchdogExeName = "WKVRCProxy.exe";
    private const int PromptTimeoutSec = 15;

    internal static readonly Regex Sha256Line =
        new(@"SHA256:\s*([0-9A-Fa-f]{64})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* best-effort */ }
        WkvrcPaths.MigrateLegacyState(Console.WriteLine);
        Logger.Install("updater");
        CrashHandler.Install("updater");
        try
        {
            string installDir = AppContext.BaseDirectory;
            string watchdogPath = Path.Combine(installDir, WatchdogExeName);
            Version current = ReadCurrentVersion(watchdogPath);
            Console.WriteLine($"Current version: {current}");

            (Version latest, string zipUrl, string tagName, string? expectedSha256) = await FetchLatestAsync();
            if (latest <= current)
            {
                Console.WriteLine("You're on the latest version.");
                return 0;
            }
            Console.WriteLine($"Update available: {tagName}");
            if (string.IsNullOrEmpty(expectedSha256))
            {
                Console.Error.WriteLine("Refusing to update: release body did not contain a SHA256: line.");
                return 12;
            }

            if (!PromptUpdate())
            {
                Console.WriteLine("Skipped.");
                return 0;
            }

            // Download → SHA verify → extract → kill-watchdog → atomic swap → relaunch.
            // The watchdog is only stopped AFTER the new payload is verified and
            // staged in temp, so a failed download leaves the user's running
            // watchdog untouched.
            string tempZip = Path.Combine(Path.GetTempPath(), $"WKVRCProxy-{tagName}.zip");
            await DownloadAsync(zipUrl, tempZip);

            string actualSha = ComputeSha256(tempZip);
            if (!actualSha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"Refusing to install: SHA256 mismatch.\n" +
                    $"  url={zipUrl}\n" +
                    $"  expected={expectedSha256}\n" +
                    $"  actual={actualSha}");
                try { File.Delete(tempZip); } catch { /* best-effort */ }
                return 13;
            }

            string tempExtract = Path.Combine(Path.GetTempPath(), $"WKVRCProxy-extract-{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(tempZip, tempExtract);

            // From here on we WILL stop the watchdog; pre-stop failures above
            // returned without touching the running install.
            StopRunningWatchdog();

            try
            {
                AtomicCopyOver(tempExtract, installDir);
            }
            catch
            {
                // CopyOver rolls back on rename failure; rethrow so the user
                // sees the error and can re-run the updater. The old install
                // is intact (atomic step never made it past the rename pass).
                throw;
            }

            try { File.Delete(tempZip); } catch { /* best-effort */ }
            try { Directory.Delete(tempExtract, true); } catch { /* best-effort */ }

            Console.WriteLine("Update installed. Relaunching watchdog…");
            Process.Start(new ProcessStartInfo
            {
                FileName = watchdogPath,
                WorkingDirectory = installDir,
                UseShellExecute = true,
            });
            return 0;
        }
        catch (Exception ex)
        {
            // Preserve exception type + (truncated) stack trace alongside the
            // message so a user pasting the failure into a bug report has
            // enough context for diagnosis.
            Console.Error.WriteLine("Updater failed: " + ex.GetType().FullName + ": " + ex.Message);
            if (ex.StackTrace != null)
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // H19: explicit error if the watchdog exe isn't sitting next to us.
    // Pre-fix the code silently fell through to the Updater's own version,
    // reporting an incorrect "current version" (the Updater's, not the
    // watchdog's) and offering spurious updates.
    private static Version ReadCurrentVersion(string watchdogPath)
    {
        if (!File.Exists(watchdogPath))
        {
            throw new InvalidOperationException(
                "WKVRCProxy.exe was not found next to the updater (expected at "
                + watchdogPath + "). The install dir may be corrupt or the "
                + "updater was launched from outside the install folder.");
        }
        var fvi = FileVersionInfo.GetVersionInfo(watchdogPath);
        if (Version.TryParse(fvi.FileVersion ?? fvi.ProductVersion ?? "0.0.0.0", out var v))
            return v;
        return new Version(0, 0, 0, 0);
    }

    // H18: tags can carry a trailing -XXXX dev-build suffix (4 hex chars per
    // build.ps1's regex). System.Version.TryParse rejects those outright.
    // Strip the suffix before parsing — the numeric part is what we compare
    // against ReadCurrentVersion's FileVersion (which never carries the
    // suffix because AssemblyVersion is pure numeric).
    private static readonly Regex DevSuffix = new(@"-[A-Fa-f0-9]{4}$", RegexOptions.Compiled);
    internal static Version ParseTagVersion(string tag)
    {
        string vNum = tag.StartsWith('v') ? tag[1..] : tag;
        vNum = DevSuffix.Replace(vNum, "");
        if (!Version.TryParse(vNum, out var v))
            throw new InvalidOperationException("Could not parse latest tag: " + tag);
        return v;
    }

    private static async Task<(Version Latest, string ZipUrl, string TagName, string? Sha256)> FetchLatestAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WKVRCProxy-Updater", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await http.GetAsync(ApiUrl);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        Version v = ParseTagVersion(tag);

        string zipUrl = "";
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zipUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                break;
            }
        }
        if (string.IsNullOrEmpty(zipUrl))
            throw new InvalidOperationException("No .zip asset on latest release.");

        // Pull the SHA256: <hex> line out of the release body. release.yml
        // always emits one; releases published by other paths won't.
        string body = doc.RootElement.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
        var match = Sha256Line.Match(body);
        string? sha = match.Success ? match.Groups[1].Value : null;

        return (v, zipUrl, tag, sha);
    }

    private static bool PromptUpdate()
    {
        // KeyAvailable throws InvalidOperationException when stdin is redirected
        // (updater piped from another tool, run from Task Scheduler, etc.).
        // In that case we treat it as headless and default to N rather than
        // crashing. A future opt-in could read a single line from stdin
        // instead, but for now silent-default-N matches the documented
        // 15s-timeout behaviour.
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("Update available — stdin is redirected, declining update silently.");
            return false;
        }
        Console.Write($"Update available — install now? [Y/N] (auto-N in {PromptTimeoutSec}s): ");
        DateTime deadline = DateTime.UtcNow.AddSeconds(PromptTimeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(intercept: false);
                    Console.WriteLine();
                    return k.Key == ConsoleKey.Y;
                }
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine();
                return false;
            }
            Thread.Sleep(100);
        }
        Console.WriteLine();
        return false;
    }

    // Console-control P/Invoke surface for graceful watchdog shutdown.
    // Without this, Process.CloseMainWindow returns false on a console
    // app (no MainWindow) and we go straight to Kill, bypassing the
    // watchdog's Ctrl+C handler — which is what writes clean_exit.flag
    // and runs the atomic restore.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
    private const uint CTRL_C_EVENT = 0;
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    // H17: prefer a graceful Ctrl+C event so the watchdog runs its real
    // shutdown (atomic restore + clean_exit.flag). Fall back to Kill only
    // if the process is still alive after the grace window.
    private static void StopRunningWatchdog()
    {
        foreach (var p in Process.GetProcessesByName("WKVRCProxy"))
        {
            using (p)
            {
                bool sentCtrlC = false;
                try
                {
                    // Detach from our own console first; the AttachConsole
                    // call below would otherwise fail with ALREADY_ATTACHED.
                    FreeConsole();
                    if (AttachConsole((uint)p.Id))
                    {
                        // Ignore the Ctrl+C in OUR process (otherwise we'd kill
                        // the updater along with the watchdog), then send to
                        // the attached console's process group.
                        SetConsoleCtrlHandler(IntPtr.Zero, true);
                        sentCtrlC = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                        FreeConsole();
                        SetConsoleCtrlHandler(IntPtr.Zero, false);
                    }
                }
                catch { /* fall back to Kill below */ }

                try
                {
                    if (sentCtrlC)
                    {
                        // Watchdog gets up to 5s to run its 12s shutdown
                        // budget. The restore is atomic (fast) so it's
                        // overwhelmingly likely to fit.
                        if (!p.WaitForExit(5000))
                        {
                            p.Kill();
                            p.WaitForExit(2000);
                        }
                    }
                    else if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                }
                catch { /* best-effort */ }
            }
        }
        Thread.Sleep(500);
    }

    private static async Task DownloadAsync(string url, string dest)
    {
        Console.WriteLine("Downloading…");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WKVRCProxy-Updater", "1.0"));
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst);
    }

    internal static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var s = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(s));
    }

    // Atomic two-pass copy: stage every file from the new payload as
    // "<dst>.new-<short>", then rename pass replaces originals via
    // File.Move(overwrite:true). On rename failure, all already-renamed
    // files are restored from the .old-<short> sidecar so a failed update
    // doesn't leave a half-old / half-new install.
    internal static void AtomicCopyOver(string from, string to)
    {
        var stagedFiles = new List<(string TempNew, string FinalDst)>();
        try
        {
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(from, file);
                string target = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Path.GetFileName(target).Equals("WKVRCProxy.Updater.exe", StringComparison.OrdinalIgnoreCase))
                    continue; // can't overwrite our own running exe
                string tempNew = target + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.Copy(file, tempNew, overwrite: true);
                stagedFiles.Add((tempNew, target));
            }
        }
        catch
        {
            // Pre-rename failure: clean up staged tmps and rethrow. Original
            // install is intact.
            foreach (var (tmp, _) in stagedFiles)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            }
            throw;
        }

        var renamed = new List<(string Backup, string FinalDst)>();
        try
        {
            foreach (var (tmp, dst) in stagedFiles)
            {
                string? backup = null;
                if (File.Exists(dst))
                {
                    backup = dst + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    File.Move(dst, backup);
                    // Register BEFORE the second move. If File.Move(tmp, dst)
                    // throws between here and line below, the rollback loop
                    // needs to know about this backup to restore it.
                    renamed.Add((backup, dst));
                }
                File.Move(tmp, dst);
            }
        }
        catch
        {
            // Rename pass failed midway. Restore previously-renamed files
            // from their .old- sidecars.
            foreach (var (backup, dst) in renamed)
            {
                try
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(backup, dst);
                }
                catch { /* nothing useful left to do */ }
            }
            // Clean up any unstaged tmps that were never renamed.
            foreach (var (tmp, _) in stagedFiles)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            }
            throw;
        }

        // Success — drop the .old-* backups.
        foreach (var (backup, _) in renamed)
        {
            try { File.Delete(backup); } catch { /* best-effort */ }
        }
    }
}
