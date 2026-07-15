using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using VrcResolver.Shared;

namespace VrcResolver.Updater;

// No flags. Running this exe IS the request to check-and-maybe-update.
//   1. Read current version from the vrcresolver.exe sitting next to us.
//   2. Hit GitHub's releases-latest API for RealWhyKnot/VRCResolver.
//   3. If newer, prompt with a 15s timeout (default No on timeout).
//   4. On Yes: download zip, verify SHA256 from release body, extract,
//      stop the running watchdog, swap files atomically, relaunch, exit.
//
// Failure-mode invariant: the watchdog is only stopped once the new
// payload has been downloaded AND SHA-verified AND extracted. Any
// failure before that step leaves the running watchdog untouched.
//
// Transition compat: installs updated across the product rename may still
// have old-named processes, mutexes, and temp artifacts around. Payload
// detection, watchdog stop/wait, and the temp sweep all accept BOTH the
// current and the pre-rename names.
internal static partial class Program
{
    private const string Repo = "RealWhyKnot/VRCResolver";
    // /releases/latest skips prereleases by GitHub convention. The list
    // endpoint includes them; we filter ourselves when the user has not
    // opted in via Maintenance.IncludePrereleases. The opt-in is read
    // directly from settings.json on disk (the Updater is a separate
    // process and can't share AppSettingsStore with the watchdog).
    private const string StableLatestUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string AnyReleasesUrl = "https://api.github.com/repos/" + Repo + "/releases?per_page=10";
    private const string WatchdogExeName = "vrcresolver.exe";
    // Pre-rename watchdog exe name. A release zip may carry a launcher
    // under this name during the transition window, and a pre-rename
    // install being updated still has a process/exe by this name.
    private const string LegacyWatchdogExeName = "WKVRCProxy.exe";
    private const string WatchdogProcessName = "vrcresolver";
    private const string LegacyWatchdogProcessName = "WKVRCProxy";
    private const string UpdaterExeName = "vrcresolver.Updater.exe";
    private const string StagedUpdaterExeName = "vrcresolver.Updater.next.exe";
    private const string ShippedManifestRelativePath = "data/release-manifest.tsv";
    private const string AssumeYesEnvVarSuffix = "UPDATE_REQUESTED";
    private const int PromptTimeoutSec = 15;

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadHardCap = TimeSpan.FromMinutes(10);

    // Anchored to start-of-line (multiline) so a release body containing
    // sample/quoted text like `` `SHA256: <fill in>` `` doesn't accidentally
    // match a placeholder before the real line. release.yml emits exactly
    // one bare-line `SHA256: <hex>`; tighten to that.
    [GeneratedRegex(@"^SHA256:\s*([0-9A-Fa-f]{64})\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Sha256LineRegex();
    internal static Regex Sha256Line => Sha256LineRegex();

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* best-effort */ }
        // Migration first so the logger opens files in the current state
        // root rather than a pre-rename one.
        AppPaths.MigrateFromLegacyProduct(Console.WriteLine);
        Logger.Install("updater");
        CrashHandler.Install("updater");
        SweepStaleTempArtifacts();
        try
        {
            string installDir = AppContext.BaseDirectory;
            string watchdogPath = Path.Combine(installDir, WatchdogExeName);
            Logger.WriteFileOnly("[updater] start installDir=" + installDir);
            Version current = ReadCurrentVersion(watchdogPath);
            Console.WriteLine($"Current version: {current}");

            (Version latest, string zipUrl, string zipName, string tagName, string? expectedSha256) = await FetchLatestAsync();
            if (latest <= current)
            {
                Console.WriteLine("You're on the latest version.");
                return 0;
            }
            Console.WriteLine($"Update available: {tagName}");
            if (string.IsNullOrEmpty(expectedSha256))
            {
                Console.Error.WriteLine("Refusing to update: release did not provide a zip SHA256.");
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
            string tempZip = Path.Combine(Path.GetTempPath(), $"vrcresolver-{tagName}.zip");
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

            string tempExtract = Path.Combine(Path.GetTempPath(), $"vrcresolver-extract-{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(tempZip, tempExtract);
            string payloadRoot = ResolvePayloadRoot(tempExtract);
            EnsureStagedUpdaterCopy(payloadRoot);

            // From here on we WILL stop the watchdog; pre-stop failures above
            // returned without touching the running install.
            StopRunningWatchdog();

            try
            {
                AtomicCopyOver(payloadRoot, installDir);
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
                WatchdogExeName + " was not found next to the updater (expected at "
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
    [GeneratedRegex(@"-[A-Fa-f0-9]{4}$")]
    private static partial Regex DevSuffixRegex();
    internal static Version ParseTagVersion(string tag)
    {
        string vNum = tag.StartsWith('v') ? tag[1..] : tag;
        vNum = DevSuffixRegex().Replace(vNum, "");
        if (!Version.TryParse(vNum, out var v))
            throw new InvalidOperationException("Could not parse latest tag: " + tag);
        return v;
    }

    private static async Task<(Version Latest, string ZipUrl, string ZipName, string TagName, string? Sha256)> FetchLatestAsync()
    {
        bool includePrereleases = ReadIncludePrereleasesFromSettings();

        // Explicit handler so corp-proxy / NTLM environments inherit Windows
        // credentials (default HttpClient leaves UseDefaultCredentials=false
        // and gets 407 Proxy Auth Required). Auto-redirect capped at 5 to
        // catch redirect loops if GitHub ever serves a misconfigured 30x.
        using var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        using var http = new HttpClient(handler) { Timeout = FetchTimeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VRCResolver-Updater", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        string apiUrl = includePrereleases ? AnyReleasesUrl : StableLatestUrl;
        Logger.WriteFileOnly("[updater] GET " + apiUrl + (includePrereleases ? " (include-prereleases=on)" : ""));
        using var resp = await http.GetAsync(apiUrl);
        Logger.WriteFileOnly("[updater] response status=" + (int)resp.StatusCode + " content-type="
            + (resp.Content.Headers.ContentType?.ToString() ?? "<none>"));
        resp.EnsureSuccessStatusCode();

        // Cloudflare's "Always Online" / GitHub's maintenance pages return
        // 200 OK with an HTML body. JsonDocument.Parse on HTML throws a
        // confusing JsonException — surface a clearer error instead.
        var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (!ct.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "GitHub releases API returned non-JSON content (Content-Type: "
                + ct + "). This usually means GitHub is returning a maintenance/error page. "
                + "Try again in a few minutes.");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement releaseElement = includePrereleases
            ? PickHighestRelease(doc.RootElement)
              ?? throw new InvalidOperationException("No releases returned by GitHub.")
            : doc.RootElement;

        string tag = releaseElement.GetProperty("tag_name").GetString() ?? "";
        Version v = ParseTagVersion(tag);

        string zipUrl = "";
        string zipName = "";
        string? zipDigest = null;
        string integrityUrl = "";
        foreach (var asset in releaseElement.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zipUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                zipName = name;
                zipDigest = asset.TryGetProperty("digest", out JsonElement digestEl) ? digestEl.GetString() : null;
                Logger.WriteFileOnly("[updater] selected asset: " + name + " url=" + zipUrl);
                continue;
            }
            if (name.EndsWith(".integrity.tsv", StringComparison.OrdinalIgnoreCase))
            {
                integrityUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                Logger.WriteFileOnly("[updater] selected integrity asset: " + name + " url=" + integrityUrl);
            }
        }
        if (string.IsNullOrEmpty(zipUrl))
            throw new InvalidOperationException("No .zip asset on latest release.");

        // Prefer the integrity TSV asset, then GitHub's asset digest. Keep
        // the body parser as a compatibility fallback for older releases.
        string body = releaseElement.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
        string? sha = await ResolveExpectedZipShaAsync(http, zipName, zipDigest, integrityUrl, body)
            .ConfigureAwait(false);
        Logger.WriteFileOnly("[updater] expected-sha256 "
            + (sha != null ? "matched (" + sha.Length + " chars)" : "absent"));

        return (v, zipUrl, zipName, tag, sha);
    }

    private static async Task<string?> ResolveExpectedZipShaAsync(
        HttpClient http,
        string zipName,
        string? zipDigest,
        string integrityUrl,
        string releaseBody)
    {
        if (!string.IsNullOrWhiteSpace(integrityUrl))
        {
            try
            {
                string integrity = await http.GetStringAsync(integrityUrl).ConfigureAwait(false);
                string? fromIntegrity = TryParseIntegritySha(integrity, zipName);
                if (fromIntegrity != null) return fromIntegrity;
            }
            catch (Exception ex)
            {
                Logger.WriteFileOnly("[updater] integrity asset read failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        string? fromDigest = TryParseAssetDigest(zipDigest);
        if (fromDigest != null) return fromDigest;

        return TryParseLegacyBodySha(releaseBody);
    }

    internal static string? TryParseIntegritySha(string integrityText, string zipName)
    {
        if (string.IsNullOrWhiteSpace(integrityText) || string.IsNullOrWhiteSpace(zipName))
            return null;

        foreach (string rawLine in integrityText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            string[] parts = line.Split('\t', 3);
            if (parts.Length != 3) continue;
            if (!parts[2].Trim().Equals(zipName, StringComparison.OrdinalIgnoreCase)) continue;
            string sha = parts[0].Trim();
            return IsSha256Hex(sha) ? sha.ToUpperInvariant() : null;
        }

        return null;
    }

    internal static string? TryParseAssetDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        string trimmed = digest.Trim();
        const string prefix = "sha256:";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string sha = trimmed[prefix.Length..].Trim();
        return IsSha256Hex(sha) ? sha.ToUpperInvariant() : null;
    }

    internal static string? TryParseLegacyBodySha(string body)
    {
        var match = Sha256Line.Match(body ?? "");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;
        foreach (char c in value)
        {
            bool hex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    // Read just the maintenance.include_prereleases flag straight from
    // settings.json on disk. The Updater is a standalone process so it
    // can't share AppSettingsStore with the watchdog; a one-shot
    // JsonDocument lookup keeps the AOT build clean (no source-gen
    // needed for one bool). Defaults to false on any read/parse error.
    private static bool ReadIncludePrereleasesFromSettings()
    {
        try
        {
            string path = Path.Combine(AppPaths.StateRoot(), "settings.json");
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("maintenance", out var maint)) return false;
            if (!maint.TryGetProperty("include_prereleases", out var flag)) return false;
            return flag.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    // List endpoint returns releases newest-published first, which isn't
    // necessarily highest-version. Pick the entry with the largest
    // parseable version so a late stable patch on an old major still
    // wins over a more-recently-published prerelease of the new major.
    // Ties (same numeric version with different prerelease flag) resolve
    // to the stable entry -- matches UpdateCheck.PickHighestFromList in
    // the watchdog so both surfaces agree on which release to install.
    internal static JsonElement? PickHighestRelease(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return null;
        Version? bestVersion = null;
        bool bestIsPrerelease = false;
        JsonElement? best = null;
        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("tag_name", out var tagEl)) continue;
            string tag = tagEl.GetString() ?? "";
            string numeric = tag.TrimStart('v', 'V');
            int dash = numeric.IndexOf('-');
            if (dash >= 0) numeric = numeric[..dash];
            if (!Version.TryParse(numeric, out Version? parsed)) continue;

            bool pre = entry.TryGetProperty("prerelease", out var preEl)
                && preEl.ValueKind == JsonValueKind.True;

            if (bestVersion != null)
            {
                int cmp = parsed.CompareTo(bestVersion);
                if (cmp < 0) continue;
                if (cmp == 0 && (pre || !bestIsPrerelease)) continue;
            }

            bestVersion = parsed;
            bestIsPrerelease = pre;
            best = entry;
        }
        return best;
    }

    private static bool PromptUpdate()
    {
        if (string.Equals(LegacyCompat.GetEnvWithLegacyFallback(AssumeYesEnvVarSuffix), "1", StringComparison.Ordinal))
        {
            Console.WriteLine("Update requested from vrcresolver; installing without a second prompt.");
            return true;
        }

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
    //
    // After the kill, poll for "no watchdog process exists AND the
    // watchdog mutexes are acquirable" with a 5s budget, rather than a
    // blind Sleep(500). Earlier impl raced against the kernel's
    // mutex-handle release on slower machines: the new watchdog process
    // launched at the end of Main() could hit AbandonedMutexException or
    // fail to acquire the named pipe instance limit, then exit silently.
    //
    // Both process names are stopped: a pre-rename watchdog may be the one
    // running when this updater installs the renamed build.
    private static void StopRunningWatchdog()
    {
        int killedCount = 0;
        var watchdogs = Process.GetProcessesByName(WatchdogProcessName)
            .Concat(Process.GetProcessesByName(LegacyWatchdogProcessName));
        foreach (var p in watchdogs)
        {
            using (p)
            {
                bool sentCtrlC = false;
                try
                {
                    FreeConsole();
                    if (AttachConsole((uint)p.Id))
                    {
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
                    killedCount++;
                }
                catch { /* best-effort */ }
            }
        }
        if (killedCount == 0) return;
        WaitForWatchdogReleaseAsync().GetAwaiter().GetResult();
    }

    // Poll for "no watchdog process exists AND mutexes acquirable" with a
    // 5s budget. Returns silently when both conditions hold; logs a
    // breadcrumb if the budget expires (caller proceeds anyway — the new
    // watchdog will surface the problem at acquisition time). Checks BOTH
    // mutex names: the renamed watchdog holds both, and a pre-rename
    // watchdog holds the legacy one.
    private static async Task WaitForWatchdogReleaseAsync()
    {
        const int BudgetMs = 5000;
        const int PollMs = 100;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < BudgetMs)
        {
            bool anyLeft = Process.GetProcessesByName(WatchdogProcessName).Length > 0
                || Process.GetProcessesByName(LegacyWatchdogProcessName).Length > 0;
            bool mutexFree = false;
            if (!anyLeft)
            {
                mutexFree = IsMutexFree("Global\\vrcresolver.Watchdog")
                    && IsMutexFree(LegacyCompat.LegacyWatchdogMutexName);
            }
            if (!anyLeft && mutexFree) return;
            await Task.Delay(PollMs).ConfigureAwait(false);
        }
        Logger.WriteFileOnly("[updater] watchdog-release wait timed out after " + sw.ElapsedMilliseconds + " ms — proceeding anyway");
    }

    private static bool IsMutexFree(string name)
    {
        try
        {
            using var m = new System.Threading.Mutex(false, name, out _);
            bool free = m.WaitOne(0);
            if (free) m.ReleaseMutex();
            return free;
        }
        catch { /* mutex creation failed (privilege?) — treat as ready */ return true; }
    }

    private static async Task DownloadAsync(string url, string dest)
    {
        Console.WriteLine("Downloading…");
        // Hard cap on the whole download so a half-open TCP from a corp
        // proxy doesn't wedge the updater forever after the watchdog has
        // already been killed. HttpClient's default Timeout doesn't apply
        // to the body stream once headers have arrived; an explicit
        // CancellationToken on CopyToAsync is the only reliable cap.
        using var cts = new CancellationTokenSource(DownloadHardCap);
        using var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        using var http = new HttpClient(handler) { Timeout = FetchTimeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VRCResolver-Updater", "1.0"));

        Logger.WriteFileOnly("[updater] download GET " + url + " dest=" + dest);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        if (total.HasValue)
        {
            // Refuse to start the download if temp drive doesn't have ~1.5×
            // the asset size free. yt-dlp + bundled artifacts mean releases
            // are routinely 250+ MB; a constrained Windows install dir can
            // run out of disk mid-write and leave the user with a stuck
            // half-zip + no clear error.
            try
            {
                var tempDrive = new DriveInfo(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\");
                long needed = (long)(total.Value * 1.5);
                if (tempDrive.AvailableFreeSpace < needed)
                {
                    throw new IOException(
                        "Insufficient free space on " + tempDrive.Name + " for download (need ~"
                        + (needed / (1024 * 1024)) + " MiB, have "
                        + (tempDrive.AvailableFreeSpace / (1024 * 1024)) + " MiB).");
                }
            }
            catch (ArgumentException) { /* path-root parse failed; skip the check */ }
            Logger.WriteFileOnly("[updater] download size=" + (total.Value / 1024) + " KiB");
        }

        await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, cts.Token);
        Logger.WriteFileOnly("[updater] download complete bytes=" + dst.Length);
    }

    // Sweep stale temp artifacts from prior failed runs so they don't pile
    // up. Extract dirs and downloaded zips older than 1 day get cleaned,
    // under both the current and the pre-rename naming patterns (leftovers
    // from pre-rename updater runs). Best-effort — failures are logged but
    // not fatal.
    private static readonly string[] TempExtractGlobs = { "vrcresolver-extract-*", "WKVRCProxy-extract-*" };
    private static readonly string[] TempZipGlobs = { "vrcresolver-*.zip", "WKVRCProxy-*.zip" };

    private static void SweepStaleTempArtifacts()
    {
        string tmp = Path.GetTempPath();
        DateTime cutoff = DateTime.UtcNow.AddDays(-1);
        int swept = 0;
        try
        {
            foreach (string glob in TempExtractGlobs)
            {
                foreach (var d in Directory.EnumerateDirectories(tmp, glob))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(d) < cutoff)
                        {
                            Directory.Delete(d, recursive: true);
                            swept++;
                        }
                    }
                    catch { /* skip */ }
                }
            }
            foreach (string glob in TempZipGlobs)
            {
                foreach (var f in Directory.EnumerateFiles(tmp, glob))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < cutoff)
                        {
                            File.Delete(f);
                            swept++;
                        }
                    }
                    catch { /* skip */ }
                }
            }
        }
        catch { /* skip */ }
        if (swept > 0) Logger.WriteFileOnly("[updater] swept " + swept + " stale temp artifacts");
    }

    internal static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var s = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(s));
    }

    // Accepts a payload keyed by EITHER watchdog exe name: current releases
    // ship vrcresolver.exe, while the transition-window release also ships
    // an old-named launcher, and a downgrade/re-run against an old zip
    // still resolves.
    internal static string ResolvePayloadRoot(string extractRoot)
    {
        if (ContainsWatchdogExe(extractRoot))
            return extractRoot;

        string[] candidates = Directory.GetDirectories(extractRoot)
            .Where(ContainsWatchdogExe)
            .ToArray();
        if (candidates.Length == 1) return candidates[0];

        throw new InvalidOperationException(
            "Release zip did not contain a recognizable payload root for " + WatchdogExeName + ".");
    }

    private static bool ContainsWatchdogExe(string dir)
    {
        return File.Exists(Path.Combine(dir, WatchdogExeName))
            || File.Exists(Path.Combine(dir, LegacyWatchdogExeName));
    }

    private static void EnsureStagedUpdaterCopy(string payloadRoot)
    {
        string updater = Path.Combine(payloadRoot, UpdaterExeName);
        if (!File.Exists(updater)) return;
        string staged = Path.Combine(payloadRoot, StagedUpdaterExeName);
        try { File.Copy(updater, staged, overwrite: true); }
        catch (Exception ex)
        {
            throw new IOException("Failed to stage updater refresh copy in extracted payload.", ex);
        }
    }

    // Atomic two-pass copy: stage every file from the new payload as
    // "<dst>.new-<short>", then rename pass replaces originals via
    // File.Move(overwrite:true). On rename failure, all already-renamed
    // files are restored from the .old-<short> sidecar so a failed update
    // doesn't leave a half-old / half-new install.
    //
    // Each rename is retried up to 3 times with 200ms backoff to absorb
    // brief AV-scanner holds on the new file. If the rollback ITSELF fails
    // for some files, those failures are collected and surfaced in the
    // rethrown exception so the user sees a manual-recovery hint instead
    // of a silent inconsistent install.
    internal static void AtomicCopyOver(string from, string to)
    {
        var stagedFiles = new List<(string TempNew, string FinalDst)>();
        HashSet<string> newManifestPaths = ReadShippedManifestPaths(from);
        HashSet<string> oldManifestPaths = ReadShippedManifestPaths(to);
        HashSet<string> deleteRelPaths = oldManifestPaths.Count > 0 && newManifestPaths.Count > 0
            ? oldManifestPaths.Except(newManifestPaths, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(from, file);
                string target = SafeInstallPath(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Path.GetFileName(target).Equals(UpdaterExeName, StringComparison.OrdinalIgnoreCase))
                    continue; // can't overwrite our own running exe
                string tempNew = target + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.Copy(file, tempNew, overwrite: true);
                stagedFiles.Add((tempNew, target));
            }
        }
        catch
        {
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
                    MoveWithRetry(dst, backup, retries: 3);
                    renamed.Add((backup, dst));
                }
                try
                {
                    MoveWithRetry(tmp, dst, retries: 3);
                }
                catch (Exception innerEx)
                {
                    // Decorate the lock case for known critical files so the
                    // user sees an actionable hint rather than a generic
                    // IOException. Tools/yt-dlp.exe is the file most likely
                    // to be locked (VRChat or the running watchdog's probe
                    // handle).
                    string fname = Path.GetFileName(dst);
                    if (innerEx is IOException
                        && fname.Equals("yt-dlp.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException(
                            "Failed to replace " + dst + " — file is locked. "
                            + "Close VRChat (it may be holding yt-dlp.exe) and re-run the updater.",
                            innerEx);
                    }
                    if (innerEx is IOException
                        && (fname.Equals(WatchdogExeName, StringComparison.OrdinalIgnoreCase)
                            || fname.Equals(LegacyWatchdogExeName, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new IOException(
                            "Failed to replace " + dst + " — watchdog process may not have fully exited yet. "
                            + "Wait a few seconds and re-run the updater.",
                            innerEx);
                    }
                    throw;
                }
            }
            foreach (string rel in deleteRelPaths)
            {
                string dst = SafeInstallPath(to, rel);
                if (!File.Exists(dst)) continue;
                string backup = dst + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                MoveWithRetry(dst, backup, retries: 3);
                renamed.Add((backup, dst));
            }
        }
        catch (Exception primaryEx)
        {
            // Rename pass failed midway. Restore previously-renamed files
            // from their .old- sidecars; collect any rollback-step failures
            // and decorate the rethrown exception with a manual-recovery
            // hint listing the files left in an unknown state.
            var rollbackFailures = new List<string>();
            foreach (var (backup, dst) in renamed)
            {
                try
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(backup, dst);
                }
                catch (Exception rbEx)
                {
                    rollbackFailures.Add(dst + " (backup at " + backup + "): " + rbEx.Message);
                }
            }
            foreach (var (tmp, _) in stagedFiles)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            }
            if (rollbackFailures.Count > 0)
            {
                throw new InvalidOperationException(
                    primaryEx.Message + "\n"
                    + "Rollback ALSO failed for " + rollbackFailures.Count + " file(s) — install may be inconsistent. "
                    + "Manual recovery: in the install dir, find each *.old-<8hex> file listed below and rename it "
                    + "back to its original name (drop the .old-* suffix).\n"
                    + string.Join("\n", rollbackFailures.Select(s => "  - " + s)),
                    primaryEx);
            }
            throw;
        }

        foreach (var (backup, _) in renamed)
        {
            try { File.Delete(backup); } catch { /* best-effort */ }
        }
    }

    internal static HashSet<string> ReadShippedManifestPaths(string root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string manifest = SafeInstallPath(root, ShippedManifestRelativePath);
        if (!File.Exists(manifest)) return paths;

        foreach (string line in File.ReadLines(manifest))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
            string[] parts = line.Split('\t', 3);
            if (parts.Length != 3) continue;
            if (!TryNormalizeRelativePath(parts[2], out string? rel)) continue;
            paths.Add(rel);
        }

        return paths;
    }

    private static bool TryNormalizeRelativePath(string path, out string rel)
    {
        rel = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        string cleaned = path.Trim().Replace('\\', '/');
        if (cleaned.StartsWith("/", StringComparison.Ordinal) || cleaned.Contains(":", StringComparison.Ordinal))
            return false;
        string[] parts = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (parts.Any(p => p == "." || p == "..")) return false;
        rel = string.Join(Path.DirectorySeparatorChar, parts);
        return true;
    }

    private static string SafeInstallPath(string root, string relative)
    {
        if (!TryNormalizeRelativePath(relative, out string? rel))
            throw new InvalidOperationException("Unsafe release manifest path: " + relative);
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(rootFull, rel));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Release path escapes install directory: " + relative);
        return full;
    }

    // Retry a File.Move up to `retries` times with 200ms backoff between
    // attempts. Absorbs AV-scanner brief holds on the source file (Defender
    // can hold a freshly-copied .new-<short> open for a tick before
    // releasing). The last attempt's exception escapes if all retries fail.
    private static void MoveWithRetry(string src, string dst, int retries)
    {
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                if (File.Exists(dst)) File.Move(src, dst, overwrite: true);
                else File.Move(src, dst);
                return;
            }
            catch (IOException) when (attempt < retries)
            {
                Thread.Sleep(200);
            }
        }
    }
}
