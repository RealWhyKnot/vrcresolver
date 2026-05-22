using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

public class FfmpegHelperTests
{
    [Fact]
    public void VersionProbe_ParsesVersionLine()
    {
        FfmpegVersionInfo? info = FfmpegVersionProbe.ParseVersion(
            "ffmpeg version 7.1.1-full_build-www.gyan.dev Copyright...\nconfiguration: --enable-gpl");

        Assert.NotNull(info);
        Assert.Equal("7.1.1-full_build-www.gyan.dev", info!.Value.Version);
    }

    [Fact]
    public void HardwareEncoderProbe_DetectsKnownH264HardwareEncoders()
    {
        const string output = """
            Encoders:
             V....D h264_nvenc           NVIDIA NVENC H.264 encoder
             V..... h264_qsv             H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (Intel Quick Sync Video acceleration)
             V....D h264_amf             AMD AMF H.264 Encoder
            """;

        IReadOnlyList<HardwareEncoderCapability> encoders = HardwareEncoderProbe.ParseEncoders(output);

        Assert.Contains(encoders, e => e.Backend == HardwareEncoderBackend.Nvenc);
        Assert.Contains(encoders, e => e.Backend == HardwareEncoderBackend.Qsv);
        Assert.Contains(encoders, e => e.Backend == HardwareEncoderBackend.Amf);
    }

    [Fact]
    public void HardwareEncoderProbe_PrefersQsvWhenAvailableToReduceDiscreteGpuContention()
    {
        var encoders = new[]
        {
            new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC"),
            new HardwareEncoderCapability("h264_qsv", HardwareEncoderBackend.Qsv, "Intel QSV"),
        };

        HardwareEncoderCapability? selected = HardwareEncoderProbe.ChoosePreferred(encoders);

        Assert.NotNull(selected);
        Assert.Equal(HardwareEncoderBackend.Nvenc, selected!.Value.Backend);
    }

    [Fact]
    public void FfmpegLocator_PrefersBundledBinary()
    {
        string temp = Path.Combine(Path.GetTempPath(), "wkvrcproxy-ffmpeg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "tools"));
        string bundled = Path.Combine(temp, "tools", "ffmpeg.exe");
        File.WriteAllText(bundled, "");

        try
        {
            FfmpegLocation? location = FfmpegLocator.Locate(temp, "");

            Assert.NotNull(location);
            Assert.Equal(FfmpegLocationKind.Bundled, location!.Value.Kind);
            Assert.Equal(bundled, location.Value.Path);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void FfmpegCapabilityProbe_FromOutputsReportsPreferredEncoder()
    {
        var location = new FfmpegLocation("ffmpeg.exe", FfmpegLocationKind.Path);
        FfmpegCapabilityProbeResult result = FfmpegCapabilityProbe.FromOutputs(
            location,
            "ffmpeg version 7.1.1-full_build-www.gyan.dev Copyright...",
            """
            Encoders:
             V....D h264_nvenc           NVIDIA NVENC H.264 encoder
             V..... h264_qsv             Intel Quick Sync Video H.264 encoder
            """);

        Assert.Equal(FfmpegCapabilityProbeStatus.Ready, result.Status);
        Assert.True(result.CanUseHardwareH264);
        Assert.NotNull(result.PreferredEncoder);
        Assert.Equal(HardwareEncoderBackend.Nvenc, result.PreferredEncoder!.Value.Backend);
        Assert.Equal("7.1.1-full_build-www.gyan.dev", result.Version!.Value.Version);
    }

    [Fact]
    public async Task FfmpegCapabilityProbe_ReportsMissingWhenExecutableIsUnavailable()
    {
        FfmpegCapabilityProbeResult result = await FfmpegCapabilityProbe.ProbeAsync(
            installDirectory: "",
            pathEnvironment: "",
            timeout: TimeSpan.FromMilliseconds(50),
            ct: CancellationToken.None,
            capture: static (_, _, _, _) => throw new InvalidOperationException("should not run"));

        Assert.Equal(FfmpegCapabilityProbeStatus.NotFound, result.Status);
        Assert.False(result.HasFfmpeg);
        Assert.False(result.CanUseHardwareH264);
    }

    [Fact]
    public async Task FfmpegCapabilityProbe_ReportsTimeoutWithoutEscapingWorkerProcess()
    {
        string temp = Path.Combine(Path.GetTempPath(), "wkvrcproxy-ffmpeg-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "tools"));
        File.WriteAllText(Path.Combine(temp, "tools", "ffmpeg.exe"), "");

        try
        {
            FfmpegCapabilityProbeResult result = await FfmpegCapabilityProbe.ProbeAsync(
                installDirectory: temp,
                pathEnvironment: "",
                timeout: TimeSpan.FromMilliseconds(10),
                ct: CancellationToken.None,
                capture: static async (_, _, _, ct) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return "";
                });

            Assert.Equal(FfmpegCapabilityProbeStatus.TimedOut, result.Status);
            Assert.False(result.CanUseHardwareH264);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void TranscodeWorkerProcess_BuildsSafeNvencSegmentCommand()
    {
        var lease = NewLease();
        var encoder = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");

        TranscodeFfmpegCommand command = TranscodeWorkerProcess.BuildSegmentCommand(
            "ffmpeg.exe",
            lease,
            encoder,
            "seg_000042.ts",
            targetWidth: 1280,
            targetHeight: 720,
            targetBitrateKbps: 2800);

        Assert.Equal("ffmpeg.exe", command.ExecutablePath);
        Assert.Contains("-nostdin", command.Arguments);
        Assert.Contains(command.Arguments, argument => argument.Contains("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", StringComparison.Ordinal));
        Assert.Contains("h264_nvenc", command.Arguments);
        AssertHardwareDecodeBeforeInput(command.Arguments, "cuda", "cuda");
        Assert.Contains(command.Arguments, argument => argument.StartsWith("scale_cuda=w=1280:h=720:", StringComparison.Ordinal));
        Assert.Contains(command.Arguments, argument => argument.Contains("format=nv12", StringComparison.Ordinal));
        Assert.Contains("-force_key_frames", command.Arguments);
        Assert.Contains("-c:a", command.Arguments);
        Assert.Contains("aac", command.Arguments);
        Assert.Contains("-headers", command.Arguments);
        Assert.Contains("Referer: https://www.youtube.com/\r\n", command.Arguments);
        Assert.Equal("seg_000042.ts", command.Arguments[^1]);
    }

    [Fact]
    public void TranscodeWorkerProcess_BuildsQsvGpuDecodeAndScaleCommand()
    {
        TranscodeFfmpegCommand command = BuildSegmentCommand(
            HardwareEncoderBackend.Qsv,
            "h264_qsv");

        Assert.Contains("h264_qsv", command.Arguments);
        AssertHardwareDecodeBeforeInput(command.Arguments, "qsv", "qsv");
        Assert.Contains(command.Arguments, argument => argument == "scale_qsv=w=1280:h=720:format=nv12");
    }

    [Theory]
    [InlineData("Amf", "h264_amf")]
    [InlineData("MediaFoundation", "h264_mf")]
    public void TranscodeWorkerProcess_BuildsD3d11DecodeForWindowsBackends(
        string backendName,
        string encoderName)
    {
        HardwareEncoderBackend backend = Enum.Parse<HardwareEncoderBackend>(backendName);
        TranscodeFfmpegCommand command = BuildSegmentCommand(backend, encoderName);

        Assert.Contains(encoderName, command.Arguments);
        AssertHardwareDecodeBeforeInput(command.Arguments, "d3d11va", "d3d11");
        Assert.Contains(command.Arguments, argument => argument == "scale=1280:720:force_original_aspect_ratio=decrease,format=nv12,hwupload=extra_hw_frames=8");
    }

    [Fact]
    public void TranscodeWorkerProcess_BuildSegmentCommandSoftwareFallbackUsesCpuScale()
    {
        var lease = NewLease();
        var encoder = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");

        TranscodeFfmpegCommand command = TranscodeWorkerProcess.BuildSegmentCommandSoftwareFallback(
            "ffmpeg.exe",
            lease,
            encoder,
            "seg_000042.ts",
            targetWidth: 1280,
            targetHeight: 720,
            targetBitrateKbps: 2800);

        Assert.DoesNotContain("-hwaccel", command.Arguments);
        Assert.DoesNotContain("-hwaccel_output_format", command.Arguments);
        Assert.Contains(command.Arguments, argument => argument == "scale=1280:720:force_original_aspect_ratio=decrease,format=yuv420p");
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("scale_cuda", StringComparison.Ordinal));
        Assert.Contains("h264_nvenc", command.Arguments);
    }

    [Fact]
    public void TranscodeWorkerProcess_UsesHigherNvencPresetForQuality()
    {
        var lease = NewLease();
        var encoder = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");

        TranscodeFfmpegCommand command = TranscodeWorkerProcess.BuildSegmentCommand(
            "ffmpeg.exe",
            lease,
            encoder,
            "seg_000042.ts",
            targetWidth: 1280,
            targetHeight: 720,
            targetBitrateKbps: 2800,
            quality: HelperEncodingQuality.Quality);

        int presetIndex = command.Arguments.ToList().IndexOf("-preset");
        Assert.True(presetIndex >= 0);
        Assert.Equal("p5", command.Arguments[presetIndex + 1]);
    }

    // Audio mapping is the load-bearing flag for the 2026-05-22 Tubi
    // no-sound incident: when has_audio=false reaches the helper, the
    // command must inject a silent lavfi input AND map from that input
    // (1:a:0). When has_audio=true the command must map the input's own
    // audio (0:a:0?) with the optional-suffix so a video-only source
    // doesn't kill the encode. These tests pin both shapes so a future
    // refactor of the synthetic-silence branch cannot silently swap them.

    [Fact]
    public void BuildSegmentCommand_WithHasAudioTrue_MapsInputAudio()
    {
        var lease = NewLease();
        var encoder = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");

        TranscodeFfmpegCommand command = TranscodeWorkerProcess.BuildSegmentCommand(
            "ffmpeg.exe", lease, encoder, "seg.ts",
            targetWidth: 1280, targetHeight: 720, targetBitrateKbps: 2800,
            hasAudio: true);

        AssertOrderedTokens(command.Arguments, "-map", "0:a:0?");
        Assert.DoesNotContain(command.Arguments, a => a.StartsWith("anullsrc", StringComparison.Ordinal));
        Assert.DoesNotContain("-shortest", command.Arguments);
        // Exactly one input URL on the command line.
        Assert.Equal(1, CountTokenOccurrences(command.Arguments, "-i"));
    }

    [Fact]
    public void BuildSegmentCommand_WithHasAudioFalse_AddsSyntheticSilenceAndMapsIt()
    {
        var lease = NewLease();
        var encoder = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");

        TranscodeFfmpegCommand command = TranscodeWorkerProcess.BuildSegmentCommand(
            "ffmpeg.exe", lease, encoder, "seg.ts",
            targetWidth: 1280, targetHeight: 720, targetBitrateKbps: 2800,
            hasAudio: false);

        Assert.Contains(command.Arguments, a => a.StartsWith(
            "anullsrc=channel_layout=stereo:sample_rate=48000",
            StringComparison.Ordinal));
        AssertOrderedTokens(command.Arguments, "-map", "1:a:0");
        Assert.Contains("-shortest", command.Arguments);
        // Two inputs: the source video, then the synthetic silence track.
        Assert.Equal(2, CountTokenOccurrences(command.Arguments, "-i"));
    }

    [Fact]
    public void BuildSegmentCommand_AlwaysPinsAacAudioParameters()
    {
        TranscodeFfmpegCommand command = BuildSegmentCommand(HardwareEncoderBackend.Nvenc, "h264_nvenc");

        AssertOrderedTokens(command.Arguments, "-c:a", "aac");
        AssertOrderedTokens(command.Arguments, "-b:a", "128k");
        AssertOrderedTokens(command.Arguments, "-ac", "2");
        AssertOrderedTokens(command.Arguments, "-ar", "48000");
    }

    [Fact]
    public void BuildSegmentCommand_SoftwareFallbackHonorsHasAudioFlag()
    {
        var lease = NewLease();
        var encoder = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");

        TranscodeFfmpegCommand withAudio = TranscodeWorkerProcess.BuildSegmentCommandSoftwareFallback(
            "ffmpeg.exe", lease, encoder, "seg.ts",
            targetWidth: 640, targetHeight: 360, targetBitrateKbps: 900,
            hasAudio: true);
        AssertOrderedTokens(withAudio.Arguments, "-map", "0:a:0?");
        Assert.DoesNotContain(withAudio.Arguments, a => a.StartsWith("anullsrc", StringComparison.Ordinal));

        TranscodeFfmpegCommand withoutAudio = TranscodeWorkerProcess.BuildSegmentCommandSoftwareFallback(
            "ffmpeg.exe", lease, encoder, "seg.ts",
            targetWidth: 640, targetHeight: 360, targetBitrateKbps: 900,
            hasAudio: false);
        Assert.Contains(withoutAudio.Arguments, a => a.StartsWith("anullsrc", StringComparison.Ordinal));
        AssertOrderedTokens(withoutAudio.Arguments, "-map", "1:a:0");
    }

    private static int CountTokenOccurrences(IReadOnlyList<string> args, string token)
    {
        int n = 0;
        foreach (string a in args)
            if (a == token) n++;
        return n;
    }

    // Asserts that `second` appears immediately after `first` somewhere
    // in the argument list. ffmpeg argv pairs are positional (e.g. -map
    // value), so adjacency is the right invariant -- not just "both
    // present somewhere".
    private static void AssertOrderedTokens(IReadOnlyList<string> args, string first, string second)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == first && args[i + 1] == second) return;
        }
        Assert.Fail($"Expected '{first} {second}' adjacent in ffmpeg argv; got: "
            + string.Join(" ", args));
    }

    [Fact]
    public void HelperBenchmark_SelectsHighestPassingQuality()
    {
        var attempts = new[]
        {
            new HelperBenchmarkAttempt(HelperEncodingQuality.Quality, true, 1.70, "ok"),
            new HelperBenchmarkAttempt(HelperEncodingQuality.Balanced, true, 1.40, "ok"),
            new HelperBenchmarkAttempt(HelperEncodingQuality.Fast, true, 2.00, "ok"),
        };

        Assert.Equal(HelperEncodingQuality.Quality, HelperBenchmarkService.SelectQuality(attempts));
    }

    [Fact]
    public void HelperBenchmark_FallsBackWhenQualityIsTooSlow()
    {
        var attempts = new[]
        {
            new HelperBenchmarkAttempt(HelperEncodingQuality.Quality, true, 1.10, "ok"),
            new HelperBenchmarkAttempt(HelperEncodingQuality.Balanced, true, 1.30, "ok"),
            new HelperBenchmarkAttempt(HelperEncodingQuality.Fast, true, 2.00, "ok"),
        };

        Assert.Equal(HelperEncodingQuality.Balanced, HelperBenchmarkService.SelectQuality(attempts));
    }

    [Fact]
    public void HelperBenchmark_FingerprintChangesWithGpuInventory()
    {
        var location = new FfmpegLocation("ffmpeg.exe", FfmpegLocationKind.Bundled);
        var probe = FfmpegCapabilityProbe.FromOutputs(
            location,
            "ffmpeg version 7.1.1",
            "Encoders:\n V....D h264_nvenc NVIDIA NVENC H.264 encoder");

        string first = HelperBenchmarkService.BuildFingerprint(probe, "gpu-a");
        string second = HelperBenchmarkService.BuildFingerprint(probe, "gpu-b");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void HelperBenchmarkCache_LoadsOnlyMatchingGpuFingerprint()
    {
        string temp = Path.Combine(Path.GetTempPath(), "wkvrcproxy-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string path = Path.Combine(temp, "helper-benchmark.json");

        try
        {
            var cache = new HelperBenchmarkCache(path);
            cache.Save(new HelperBenchmarkRecord
            {
                Fingerprint = "gpu-a",
                SelectedQuality = "quality",
                RealtimeFactor = 2.0,
                Encoder = "h264_nvenc",
                EncoderBackend = "nvenc",
                FfmpegVersion = "7.1.1",
                TestedAtUtc = DateTime.UtcNow.ToString("o"),
            });

            Assert.True(cache.TryLoad("gpu-a", out HelperBenchmarkRecord matching));
            Assert.Equal("quality", matching.SelectedQuality);
            Assert.False(cache.TryLoad("gpu-b", out _));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void HelperSelfThrottle_PausesOnBatteryByDefault()
    {
        var settings = new AppSettings().Normalize();

        HelperThrottleDecision decision = HelperSelfThrottle.Evaluate(
            settings,
            new HelperRuntimeSignals(
                OnBattery: true,
                VrChatRunning: true,
                GpuBusyPercent: 10,
                CpuBusyPercent: 10,
                ThermalHeadroomPercent: 50,
                UploadQueueBytes: 0,
                ConsecutiveFailures: 0));

        Assert.False(decision.CanAcceptWork);
        Assert.Equal("on battery", decision.Reason);
    }

    [Fact]
    public void HelperSelfThrottle_AllowsWorkWhenSignalsAreHealthy()
    {
        var settings = new AppSettings().Normalize();

        HelperThrottleDecision decision = HelperSelfThrottle.Evaluate(
            settings,
            new HelperRuntimeSignals(
                OnBattery: false,
                VrChatRunning: true,
                GpuBusyPercent: 20,
                CpuBusyPercent: 20,
                ThermalHeadroomPercent: 50,
                UploadQueueBytes: 0,
                ConsecutiveFailures: 0));

        Assert.True(decision.CanAcceptWork);
        Assert.Equal("idle", decision.State);
    }

    private static TranscodeLease NewLease()
    {
        return new TranscodeLease(
            JobId: "job",
            PlaybackId: "playback",
            Rendition: "720p30_h264",
            SegmentIndex: 42,
            StartPtsSeconds: 84.0,
            DurationSeconds: 2.0,
            DeadlineMilliseconds: 6000,
            LeaseId: "lease",
            InputChunkUrl: new Uri("https://node1.whyknot.dev/helper/input/job/42.ts"),
            OutputSpec: new TranscodeOutputSpec(
                Codec: "h264",
                PixelFormat: "yuv420p",
                Profile: "high",
                GopSeconds: 2,
                Audio: "server"));
    }

    private static TranscodeFfmpegCommand BuildSegmentCommand(
        HardwareEncoderBackend backend,
        string encoderName)
    {
        var encoder = new HardwareEncoderCapability(encoderName, backend, encoderName);
        return TranscodeWorkerProcess.BuildSegmentCommand(
            "ffmpeg.exe",
            NewLease(),
            encoder,
            "seg_000042.ts",
            targetWidth: 1280,
            targetHeight: 720,
            targetBitrateKbps: 2800);
    }

    private static void AssertHardwareDecodeBeforeInput(
        IReadOnlyList<string> arguments,
        string hwaccel,
        string outputFormat)
    {
        var args = arguments.ToList();
        int inputIndex = args.IndexOf("-i");
        int hwaccelIndex = args.IndexOf("-hwaccel");
        int outputFormatIndex = args.IndexOf("-hwaccel_output_format");

        Assert.True(inputIndex >= 0);
        Assert.True(hwaccelIndex >= 0 && hwaccelIndex < inputIndex);
        Assert.Equal(hwaccel, args[hwaccelIndex + 1]);
        Assert.True(outputFormatIndex >= 0 && outputFormatIndex < inputIndex);
        Assert.Equal(outputFormat, args[outputFormatIndex + 1]);
    }
}
