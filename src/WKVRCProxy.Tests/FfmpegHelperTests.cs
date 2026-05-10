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
        Assert.Equal(HardwareEncoderBackend.Qsv, selected!.Value.Backend);
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
        Assert.Contains("h264_nvenc", command.Arguments);
        Assert.Contains(command.Arguments, argument => argument.Contains("format=yuv420p", StringComparison.Ordinal));
        Assert.Contains("-force_key_frames", command.Arguments);
        Assert.Contains("-an", command.Arguments);
        Assert.Equal("seg_000042.ts", command.Arguments[^1]);
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
}
