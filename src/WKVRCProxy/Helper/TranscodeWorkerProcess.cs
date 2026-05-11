using System.Diagnostics;
using System.Globalization;

namespace WKVRCProxy;

internal sealed record TranscodeFfmpegCommand(
    string ExecutablePath,
    IReadOnlyList<string> Arguments)
{
    public ProcessStartInfo ToStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in Arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }
}

internal static class TranscodeWorkerProcess
{
    private const string HttpUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static TranscodeFfmpegCommand BuildSegmentCommand(
        string ffmpegPath,
        TranscodeLease lease,
        HardwareEncoderCapability encoder,
        string outputPath,
        int targetWidth,
        int targetHeight,
        int targetBitrateKbps,
        bool hasAudio = true,
        int audioBitrateKbps = 128,
        HelperEncodingQuality quality = HelperEncodingQuality.Fast)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            throw new ArgumentException("FFmpeg path is required.", nameof(ffmpegPath));
        if (lease == null)
            throw new ArgumentNullException(nameof(lease));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        int safeWidth = Math.Clamp(targetWidth, 160, 3840);
        int safeHeight = Math.Clamp(targetHeight, 120, 2160);
        int bitrate = Math.Clamp(targetBitrateKbps, 300, 12000);
        int audioBitrate = Math.Clamp(audioBitrateKbps, 64, 320);
        int gopSeconds = Math.Clamp(lease.OutputSpec.GopSeconds <= 0 ? 2 : lease.OutputSpec.GopSeconds, 1, 6);
        int gopFrames = gopSeconds * 30;
        string pixFmt = PixelFormatFor(encoder, lease.OutputSpec.PixelFormat);
        string segment = lease.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string start = lease.StartPtsSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-loglevel",
            "error",
            "-protocol_whitelist",
            "file,http,https,tcp,tls,crypto",
            "-rw_timeout",
            "15000000",
            "-user_agent",
            HttpUserAgent,
            "-headers",
            "Referer: https://www.youtube.com/\r\n",
            "-fflags",
            "+genpts+discardcorrupt",
            "-ss",
            start,
            "-i",
            lease.InputChunkUrl.ToString(),
        };

        if (!hasAudio)
        {
            args.AddRange(new[]
            {
                "-f",
                "lavfi",
                "-t",
                segment,
                "-i",
                "anullsrc=channel_layout=stereo:sample_rate=48000",
            });
        }

        args.AddRange(new[]
        {
            "-t",
            segment,
            "-map",
            "0:v:0",
            "-sn",
            "-dn",
        });

        if (hasAudio)
        {
            args.AddRange(new[] { "-map", "0:a:0?" });
        }
        else
        {
            args.AddRange(new[] { "-map", "1:a:0", "-shortest" });
        }

        args.AddRange(new[]
        {
            "-vf",
            "scale=" + safeWidth.ToString(CultureInfo.InvariantCulture) + ":"
                + safeHeight.ToString(CultureInfo.InvariantCulture)
                + ":force_original_aspect_ratio=decrease,format=" + pixFmt,
            "-c:v",
            encoder.EncoderName,
        });

        AddBackendOptions(args, encoder, NormalizeQuality(quality));

        args.AddRange(new[]
        {
            "-b:v",
            bitrate.ToString(CultureInfo.InvariantCulture) + "k",
            "-maxrate",
            Math.Max(bitrate, (int)(bitrate * 1.15)).ToString(CultureInfo.InvariantCulture) + "k",
            "-bufsize",
            Math.Max(bitrate * 2, 600).ToString(CultureInfo.InvariantCulture) + "k",
            "-g",
            gopFrames.ToString(CultureInfo.InvariantCulture),
            "-keyint_min",
            gopFrames.ToString(CultureInfo.InvariantCulture),
            "-force_key_frames",
            "expr:gte(t,n_forced*" + gopSeconds.ToString(CultureInfo.InvariantCulture) + ")",
            "-bf",
            "0",
            "-c:a",
            "aac",
            "-b:a",
            audioBitrate.ToString(CultureInfo.InvariantCulture) + "k",
            "-ac",
            "2",
            "-ar",
            "48000",
            "-output_ts_offset",
            start,
            "-avoid_negative_ts",
            "disabled",
            "-f",
            "mpegts",
            outputPath,
        });

        return new TranscodeFfmpegCommand(ffmpegPath, args);
    }

    public static void ApplySafePriority(Process process)
    {
        if (process == null)
            throw new ArgumentNullException(nameof(process));

        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* priority changes are best-effort */ }
    }

    private static string PixelFormatFor(HardwareEncoderCapability encoder, string requested)
    {
        requested = (requested ?? "").Trim().ToLowerInvariant();
        if (requested is "yuv420p" or "nv12")
            return encoder.Backend is HardwareEncoderBackend.Qsv or HardwareEncoderBackend.Amf or HardwareEncoderBackend.MediaFoundation
                ? "nv12"
                : requested;

        return encoder.Backend is HardwareEncoderBackend.Qsv or HardwareEncoderBackend.Amf or HardwareEncoderBackend.MediaFoundation
            ? "nv12"
            : "yuv420p";
    }

    internal static HelperEncodingQuality NormalizeQuality(HelperEncodingQuality quality)
    {
        return quality is HelperEncodingQuality.Balanced or HelperEncodingQuality.Quality
            ? quality
            : HelperEncodingQuality.Fast;
    }

    internal static IReadOnlyList<string> BackendOptionsFor(HardwareEncoderCapability encoder, HelperEncodingQuality quality)
    {
        var args = new List<string>(10);
        AddBackendOptions(args, encoder, NormalizeQuality(quality));
        return args;
    }

    private static void AddBackendOptions(List<string> args, HardwareEncoderCapability encoder, HelperEncodingQuality quality)
    {
        switch (encoder.Backend)
        {
            case HardwareEncoderBackend.Nvenc:
                args.AddRange(new[]
                {
                    "-preset", quality switch
                    {
                        HelperEncodingQuality.Quality => "p5",
                        HelperEncodingQuality.Balanced => "p4",
                        _ => "p2",
                    },
                    "-tune", "ll",
                    "-rc", "cbr",
                    "-rc-lookahead", quality == HelperEncodingQuality.Quality ? "8" : "0",
                    "-forced-idr", "1",
                });
                break;
            case HardwareEncoderBackend.Qsv:
                args.AddRange(new[]
                {
                    "-preset", quality switch
                    {
                        HelperEncodingQuality.Quality => "medium",
                        HelperEncodingQuality.Balanced => "fast",
                        _ => "veryfast",
                    },
                    "-low_power", quality == HelperEncodingQuality.Fast ? "1" : "0",
                });
                break;
            case HardwareEncoderBackend.Amf:
                args.AddRange(new[]
                {
                    "-quality", quality switch
                    {
                        HelperEncodingQuality.Quality => "quality",
                        HelperEncodingQuality.Balanced => "balanced",
                        _ => "speed",
                    },
                });
                break;
            case HardwareEncoderBackend.MediaFoundation:
                args.AddRange(new[] { "-hw_encoding", "1", "-rate_control", "cbr" });
                break;
        }
    }
}
