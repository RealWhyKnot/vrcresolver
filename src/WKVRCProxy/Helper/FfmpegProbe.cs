using System.Diagnostics;

namespace WKVRCProxy;

internal enum HardwareEncoderBackend
{
    Nvenc,
    Qsv,
    Amf,
    MediaFoundation,
}

internal readonly record struct FfmpegVersionInfo(string RawLine, string Version);

internal readonly record struct HardwareEncoderCapability(
    string EncoderName,
    HardwareEncoderBackend Backend,
    string DisplayName);

internal sealed class GpuCapabilitySnapshot
{
    public GpuCapabilitySnapshot(FfmpegVersionInfo? ffmpegVersion, IReadOnlyList<HardwareEncoderCapability> encoders)
    {
        FfmpegVersion = ffmpegVersion;
        Encoders = encoders ?? throw new ArgumentNullException(nameof(encoders));
    }

    public FfmpegVersionInfo? FfmpegVersion { get; }
    public IReadOnlyList<HardwareEncoderCapability> Encoders { get; }
    public bool HasHardwareH264 => Encoders.Count > 0;
}

internal static class FfmpegVersionProbe
{
    public static FfmpegVersionInfo? ParseVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        string? line = output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static value => value.StartsWith("ffmpeg version ", StringComparison.OrdinalIgnoreCase));
        if (line == null)
            return null;

        string rest = line["ffmpeg version ".Length..].Trim();
        string version = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? rest;
        return new FfmpegVersionInfo(line, version);
    }

    public static Task<string> CaptureAsync(string ffmpegPath, string argument, TimeSpan timeout, CancellationToken ct)
    {
        return CaptureAsync(ffmpegPath, new[] { argument }, timeout, ct);
    }

    public static async Task<string> CaptureAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            throw new ArgumentException("FFmpeg path is required.", nameof(ffmpegPath));

        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        foreach (string argument in arguments ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(argument))
                process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return stdout + "\n" + stderr;
    }
}

internal static class HardwareEncoderProbe
{
    private static readonly (string Name, HardwareEncoderBackend Backend, string Display)[] s_known =
    [
        ("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC"),
        ("h264_qsv", HardwareEncoderBackend.Qsv, "Intel QSV"),
        ("h264_amf", HardwareEncoderBackend.Amf, "AMD AMF"),
        ("h264_mf", HardwareEncoderBackend.MediaFoundation, "Windows Media Foundation"),
    ];

    public static IReadOnlyList<HardwareEncoderCapability> ParseEncoders(string encoderListOutput)
    {
        if (string.IsNullOrWhiteSpace(encoderListOutput))
            return Array.Empty<HardwareEncoderCapability>();

        var found = new List<HardwareEncoderCapability>(s_known.Length);
        foreach (var known in s_known)
        {
            if (ContainsEncoder(encoderListOutput, known.Name))
                found.Add(new HardwareEncoderCapability(known.Name, known.Backend, known.Display));
        }

        return found;
    }

    public static HardwareEncoderCapability? ChoosePreferred(IReadOnlyList<HardwareEncoderCapability> encoders)
    {
        if (encoders == null || encoders.Count == 0)
            return null;

        HardwareEncoderBackend[] order =
        [
            HardwareEncoderBackend.Nvenc,
            HardwareEncoderBackend.Amf,
            HardwareEncoderBackend.Qsv,
            HardwareEncoderBackend.MediaFoundation,
        ];

        foreach (HardwareEncoderBackend backend in order)
        {
            for (int i = 0; i < encoders.Count; i++)
                if (encoders[i].Backend == backend)
                    return encoders[i];
        }

        return encoders[0];
    }

    private static bool ContainsEncoder(string output, string encoderName)
    {
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Encoders:", StringComparison.OrdinalIgnoreCase))
                continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 1; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], encoderName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
