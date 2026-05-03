using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace WKVRCProxy.Updater;

// No flags. Running this exe IS the request to check-and-maybe-update.
//   1. Read current version from the WKVRCProxy.exe sitting next to us.
//   2. Hit GitHub's releases-latest API for RealWhyKnot/WKVRCProxy.
//   3. If newer, prompt with a 15s timeout (default No on timeout).
//   4. On Yes: stop the running watchdog, download asset zip, extract over the
//      install dir, relaunch the watchdog, exit.
internal static class Program
{
    private const string Repo = "RealWhyKnot/WKVRCProxy";
    private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string WatchdogExeName = "WKVRCProxy.exe";
    private const string MutexName = "Global\\WKVRCProxy.Watchdog";
    private const int PromptTimeoutSec = 15;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            string installDir = AppContext.BaseDirectory;
            string watchdogPath = Path.Combine(installDir, WatchdogExeName);
            Version current = ReadCurrentVersion(watchdogPath);
            Console.WriteLine($"Current version: {current}");

            (Version latest, string zipUrl, string tagName) = await FetchLatestAsync();
            if (latest <= current)
            {
                Console.WriteLine("You're on the latest version.");
                return 0;
            }
            Console.WriteLine($"Update available: {tagName}");

            if (!PromptUpdate())
            {
                Console.WriteLine("Skipped.");
                return 0;
            }

            StopRunningWatchdog();
            string tempZip = Path.Combine(Path.GetTempPath(), $"WKVRCProxy-{tagName}.zip");
            await DownloadAsync(zipUrl, tempZip);

            string tempExtract = Path.Combine(Path.GetTempPath(), $"WKVRCProxy-extract-{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(tempZip, tempExtract);

            CopyOver(tempExtract, installDir);
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
            Console.Error.WriteLine("Updater failed: " + ex.Message);
            return 1;
        }
    }

    private static Version ReadCurrentVersion(string watchdogPath)
    {
        if (File.Exists(watchdogPath))
        {
            var fvi = FileVersionInfo.GetVersionInfo(watchdogPath);
            if (Version.TryParse(fvi.FileVersion ?? fvi.ProductVersion ?? "0.0.0.0", out var v)) return v;
        }
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
    }

    private static async Task<(Version, string, string)> FetchLatestAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WKVRCProxy-Updater", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await http.GetAsync(ApiUrl);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        string vNum = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(vNum, out var v))
            throw new InvalidOperationException("Could not parse latest tag: " + tag);

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
        return (v, zipUrl, tag);
    }

    private static bool PromptUpdate()
    {
        Console.Write($"Update available — install now? [Y/N] (auto-N in {PromptTimeoutSec}s): ");
        DateTime deadline = DateTime.UtcNow.AddSeconds(PromptTimeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            if (Console.KeyAvailable)
            {
                var k = Console.ReadKey(intercept: false);
                Console.WriteLine();
                return k.Key == ConsoleKey.Y;
            }
            Thread.Sleep(100);
        }
        Console.WriteLine();
        return false;
    }

    private static void StopRunningWatchdog()
    {
        // Try a clean close first (CloseMainWindow against the console window owner),
        // then fall back to Kill if it's still alive after the grace window.
        foreach (var p in Process.GetProcessesByName("WKVRCProxy"))
        {
            try
            {
                if (!p.CloseMainWindow()) p.Kill();
                p.WaitForExit(5000);
            }
            catch { /* best-effort */ }
        }
        // Drain the mutex hold-time — give the OS a beat to release locks on the exe.
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

    private static void CopyOver(string from, string to)
    {
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, file);
            string target = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Skip the updater itself — we cannot overwrite our own running exe.
            if (Path.GetFileName(target).Equals("WKVRCProxy.Updater.exe", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, target, true);
        }
    }
}
