using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal sealed class HelperBenchmarkRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string Fingerprint { get; set; } = "";
    public string SelectedQuality { get; set; } = HelperEncodingQualityNames.Fast;
    public double RealtimeFactor { get; set; }
    public string Encoder { get; set; } = "";
    public string EncoderBackend { get; set; } = "";
    public string FfmpegVersion { get; set; } = "";
    public string TestedAtUtc { get; set; } = "";
}

internal readonly record struct HelperBenchmarkAttempt(
    HelperEncodingQuality Quality,
    bool Success,
    double RealtimeFactor,
    string Message);

internal static class HelperBenchmarkService
{
    private const double BenchmarkDurationSeconds = 1.25;
    private static readonly TimeSpan BenchmarkTimeout = TimeSpan.FromSeconds(7);
    private static string? s_gpuInventory;

    public static async Task<HelperEncodingQuality> ResolveQualityAsync(
        AppSettings settings,
        FfmpegCapabilityProbeResult probe,
        CancellationToken ct)
    {
        if (!settings.Helper.GpuSharing)
            return HelperEncodingQuality.Fast;

        HelperEncodingQuality requested = HelperEncodingQualityNames.ParseOrAuto(settings.Helper.EncodingQuality);
        if (requested != HelperEncodingQuality.Auto)
            return TranscodeWorkerProcess.NormalizeQuality(requested);

        if (!probe.Location.HasValue || !probe.PreferredEncoder.HasValue)
            return HelperEncodingQuality.Fast;

        string fingerprint = BuildFingerprint(probe, ReadGpuInventory());
        var cache = new HelperBenchmarkCache(DefaultCachePath());
        if (cache.TryLoad(fingerprint, out HelperBenchmarkRecord? cached))
            return TranscodeWorkerProcess.NormalizeQuality(HelperEncodingQualityNames.ParseOrAuto(cached.SelectedQuality));

        HelperBenchmarkAttempt attempt = await RunBenchmarkAsync(
            probe.Location.Value.Path,
            probe.PreferredEncoder.Value,
            ct).ConfigureAwait(false);

        HelperEncodingQuality selected = attempt.Success
            ? TranscodeWorkerProcess.NormalizeQuality(attempt.Quality)
            : HelperEncodingQuality.Fast;

        cache.Save(new HelperBenchmarkRecord
        {
            SchemaVersion = 1,
            Fingerprint = fingerprint,
            SelectedQuality = HelperEncodingQualityNames.Format(selected),
            RealtimeFactor = attempt.RealtimeFactor,
            Encoder = probe.PreferredEncoder.Value.EncoderName,
            EncoderBackend = probe.PreferredEncoder.Value.Backend.ToString().ToLowerInvariant(),
            FfmpegVersion = probe.Version?.Version ?? "",
            TestedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        });

        Logger.WriteFileOnly("[helper][benchmark] selected quality="
            + HelperEncodingQualityNames.Format(selected)
            + " encoder=" + probe.PreferredEncoder.Value.EncoderName
            + " realtime=" + attempt.RealtimeFactor.ToString("0.00", CultureInfo.InvariantCulture)
            + " fingerprint=" + fingerprint[..Math.Min(12, fingerprint.Length)]);
        return selected;
    }

    internal static HelperEncodingQuality SelectQuality(IReadOnlyList<HelperBenchmarkAttempt> attempts)
    {
        foreach (HelperBenchmarkAttempt attempt in attempts)
        {
            if (!attempt.Success)
                continue;
            double threshold = attempt.Quality switch
            {
                HelperEncodingQuality.Quality => 1.65,
                HelperEncodingQuality.Balanced => 1.25,
                _ => 0.90,
            };
            if (attempt.RealtimeFactor >= threshold)
                return TranscodeWorkerProcess.NormalizeQuality(attempt.Quality);
        }

        return HelperEncodingQuality.Fast;
    }

    internal static string BuildFingerprint(FfmpegCapabilityProbeResult probe, string gpuInventory)
    {
        string encoder = probe.PreferredEncoder?.EncoderName ?? "";
        string backend = probe.PreferredEncoder?.Backend.ToString() ?? "";
        string version = probe.Version?.Version ?? "";
        string raw = version + "\n" + encoder + "\n" + backend + "\n" + (gpuInventory ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static async Task<HelperBenchmarkAttempt> RunBenchmarkAsync(
        string ffmpegPath,
        HardwareEncoderCapability encoder,
        CancellationToken ct)
    {
        var attempts = new List<HelperBenchmarkAttempt>(3);
        foreach (HelperEncodingQuality quality in new[]
                 {
                     HelperEncodingQuality.Quality,
                     HelperEncodingQuality.Balanced,
                     HelperEncodingQuality.Fast,
                 })
        {
            HelperBenchmarkAttempt attempt = await TryRunAttemptAsync(ffmpegPath, encoder, quality, ct)
                .ConfigureAwait(false);
            attempts.Add(attempt);
            if (SelectQuality(attempts) == TranscodeWorkerProcess.NormalizeQuality(quality))
                return attempt;
        }

        HelperEncodingQuality selected = SelectQuality(attempts);
        return attempts.FirstOrDefault(a => a.Quality == selected && a.Success, attempts[^1]);
    }

    private static async Task<HelperBenchmarkAttempt> TryRunAttemptAsync(
        string ffmpegPath,
        HardwareEncoderCapability encoder,
        HelperEncodingQuality quality,
        CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-f",
            "lavfi",
            "-i",
            "testsrc2=size=1280x720:rate=30",
            "-t",
            BenchmarkDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-vf",
            "format=" + (encoder.Backend is HardwareEncoderBackend.Qsv or HardwareEncoderBackend.Amf or HardwareEncoderBackend.MediaFoundation
                ? "nv12"
                : "yuv420p"),
            "-c:v",
            encoder.EncoderName,
        };
        args.AddRange(TranscodeWorkerProcess.BackendOptionsFor(encoder, quality));
        args.AddRange(new[]
        {
            "-b:v", "3500k",
            "-maxrate", "4200k",
            "-bufsize", "7000k",
            "-g", "60",
            "-bf", "0",
            "-an",
            "-f", "null",
            "-",
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(BenchmarkTimeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = ffmpegPath;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            foreach (string arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();
            TranscodeWorkerProcess.ApplySafePriority(process);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                return new HelperBenchmarkAttempt(quality, false, 0, "timeout");
            }
            _ = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            double realtime = sw.Elapsed.TotalSeconds <= 0
                ? 0
                : BenchmarkDurationSeconds / sw.Elapsed.TotalSeconds;
            return new HelperBenchmarkAttempt(
                quality,
                process.ExitCode == 0,
                realtime,
                process.ExitCode == 0 ? "ok" : Snip(stderr, 160));
        }
        catch (Exception ex)
        {
            return new HelperBenchmarkAttempt(quality, false, 0, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string DefaultCachePath()
        => Path.Combine(WkvrcPaths.StateRoot(), "helper-benchmark.json");

    private static string ReadGpuInventory()
    {
        if (s_gpuInventory != null)
            return s_gpuInventory;

        s_gpuInventory = CaptureGpuInventory() ?? Environment.MachineName;
        return s_gpuInventory;
    }

    private static string? CaptureGpuInventory()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "wmic.exe";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.ArgumentList.Add("path");
            process.StartInfo.ArgumentList.Add("win32_VideoController");
            process.StartInfo.ArgumentList.Add("get");
            process.StartInfo.ArgumentList.Add("Name,PNPDeviceID,DriverVersion");
            process.StartInfo.ArgumentList.Add("/format:list");
            process.Start();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string Snip(string value, int max)
    {
        value = (value ?? "").Trim();
        return value.Length <= max ? value : value[..Math.Max(0, max - 2)] + "..";
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}

internal sealed class HelperBenchmarkCache
{
    private readonly string _path;

    public HelperBenchmarkCache(string path)
    {
        _path = path;
    }

    public bool TryLoad(string fingerprint, out HelperBenchmarkRecord record)
    {
        record = new HelperBenchmarkRecord();
        try
        {
            if (!File.Exists(_path))
                return false;

            using var stream = File.OpenRead(_path);
            HelperBenchmarkRecord? loaded = JsonSerializer.Deserialize(stream, MeshJsonContext.Default.HelperBenchmarkRecord);
            if (loaded == null || loaded.SchemaVersion != 1)
                return false;
            if (!string.Equals(loaded.Fingerprint, fingerprint, StringComparison.Ordinal))
                return false;

            record = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save(HelperBenchmarkRecord record)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string tmp = _path + ".new";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, record, MeshJsonContext.Default.HelperBenchmarkRecord);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.WriteFileOnly("[helper][benchmark] cache save failed: "
                + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
        }
    }
}
