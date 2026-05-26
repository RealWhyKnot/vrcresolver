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
        => BuildSegmentCommandCore(
            ffmpegPath,
            lease,
            encoder,
            outputPath,
            targetWidth,
            targetHeight,
            targetBitrateKbps,
            hasAudio,
            audioBitrateKbps,
            quality,
            useHardwareDecode: true);

    public static TranscodeFfmpegCommand BuildSegmentCommandSoftwareFallback(
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
        => BuildSegmentCommandCore(
            ffmpegPath,
            lease,
            encoder,
            outputPath,
            targetWidth,
            targetHeight,
            targetBitrateKbps,
            hasAudio,
            audioBitrateKbps,
            quality,
            useHardwareDecode: false);

    private static TranscodeFfmpegCommand BuildSegmentCommandCore(
        string ffmpegPath,
        TranscodeLease lease,
        HardwareEncoderCapability encoder,
        string outputPath,
        int targetWidth,
        int targetHeight,
        int targetBitrateKbps,
        bool hasAudio,
        int audioBitrateKbps,
        HelperEncodingQuality quality,
        bool useHardwareDecode)
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
        string segment = lease.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string start = lease.StartPtsSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string videoFilter = useHardwareDecode
            ? HardwareScaleFilterFor(encoder, safeWidth, safeHeight)
            : SoftwareScaleFilterFor(encoder, lease.OutputSpec.PixelFormat, safeWidth, safeHeight);

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
            // +discardcorrupt silently drops HEVC decoder warmup frames -- on
            // Tubi seg_0 the encoder output ran ~0.75s short of the lease window,
            // which fell outside the server's video_duration tolerance and the
            // entire window was rejected. Server-side encoder dropped this flag
            // for the same reason in FfmpegSegmentEncoder / LazyHls.
            "-fflags",
            "+genpts",
        };

        if (useHardwareDecode)
            args.AddRange(HardwareDecodeOptionsFor(encoder));

        args.AddRange(new[]
        {
            "-ss",
            start,
            "-i",
            lease.InputChunkUrl.ToString(),
        });

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
            videoFilter,
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
            // SPS/PPS at every IDR so each TS segment is independently decodable.
            // Without this, segments after the first land in the muxer without
            // parameter sets and strict decoders refuse to initialize.
            "-bsf:v",
            "dump_extra=freq=keyframe",
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

    private static IReadOnlyList<string> HardwareDecodeOptionsFor(HardwareEncoderCapability encoder)
    {
        return encoder.Backend switch
        {
            // -extra_hw_frames widens NVDEC's reference-frame pool. Some HEVC sources
            // (Tubi's catalog among them) use longer reference chains than the cuvid
            // default 25 covers and surface as "Could not find ref with POC N" decoder
            // warnings followed by a black/short segment. Bumping the pool stops the
            // decoder from dropping reference frames it still needs. 16 was the first
            // bump; observed Tubi content at certain time offsets still overruns it
            // and the encoder writes only ~0.7-1 s of video out of the 4 s window
            // before NVDEC drops the chain. 32 covers the deepest reference chains
            // seen in the wild without meaningfully growing GPU memory.
            HardwareEncoderBackend.Nvenc => new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-extra_hw_frames", "32" },
            HardwareEncoderBackend.Qsv => new[] { "-hwaccel", "qsv", "-hwaccel_output_format", "qsv" },
            HardwareEncoderBackend.Amf or HardwareEncoderBackend.MediaFoundation =>
                new[] { "-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11" },
            _ => Array.Empty<string>(),
        };
    }

    private static string HardwareScaleFilterFor(HardwareEncoderCapability encoder, int safeWidth, int safeHeight)
    {
        string width = safeWidth.ToString(CultureInfo.InvariantCulture);
        string height = safeHeight.ToString(CultureInfo.InvariantCulture);
        return encoder.Backend switch
        {
            // `format=nv12` must be part of scale_cuda's option list (colon-
            // separated), not a separate filter. The previous form used a comma,
            // which made ffmpeg parse `format=nv12` as a standalone CPU-side
            // filter; an auto_scale would then be inserted to bridge the CUDA
            // surface from scale_cuda to the CPU format filter and fail with
            // "Impossible to convert between the formats supported by the
            // filter 'Parsed_scale_cuda_0' and the filter 'auto_scale_0'".
            HardwareEncoderBackend.Nvenc =>
                "scale_cuda=w=" + width + ":h=" + height + ":format=nv12:force_original_aspect_ratio=decrease",
            HardwareEncoderBackend.Qsv =>
                "scale_qsv=w=" + width + ":h=" + height + ":format=nv12",
            HardwareEncoderBackend.Amf or HardwareEncoderBackend.MediaFoundation =>
                "scale=" + width + ":" + height + ":force_original_aspect_ratio=decrease,format=nv12,hwupload=extra_hw_frames=8",
            _ => SoftwareScaleFilterFor(encoder, "yuv420p", safeWidth, safeHeight),
        };
    }

    private static string SoftwareScaleFilterFor(
        HardwareEncoderCapability encoder,
        string requestedPixelFormat,
        int safeWidth,
        int safeHeight)
    {
        return "scale=" + safeWidth.ToString(CultureInfo.InvariantCulture) + ":"
            + safeHeight.ToString(CultureInfo.InvariantCulture)
            + ":force_original_aspect_ratio=decrease,format=" + PixelFormatFor(encoder, requestedPixelFormat);
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
