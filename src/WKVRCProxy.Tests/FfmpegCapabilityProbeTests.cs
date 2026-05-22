using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

public class FfmpegCapabilityProbeTests
{
    // Simulate a smoke runner where the first encoder (nvenc) always fails,
    // second (amf) always passes. Verifies the demotion + retry logic
    // across the two discrete-GPU backends. Integrated backends (qsv, mf)
    // are no longer eligible at all, so they don't participate in the
    // demote-and-retry chain.
    [Fact]
    public async Task SmokeTest_DemotesFailingEncoderAndTriesNext()
    {
        var nvenc = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");
        var amf = new HardwareEncoderCapability("h264_amf", HardwareEncoderBackend.Amf, "AMD AMF");
        var location = new FfmpegLocation("ffmpeg.exe", FfmpegLocationKind.Bundled);

        var base_ = new FfmpegCapabilityProbeResult(
            location,
            new FfmpegVersionInfo("ffmpeg version 7.1", "7.1"),
            new[] { nvenc, amf },
            nvenc,
            FfmpegCapabilityProbeStatus.Ready,
            "ok");

        var attemptLog = new List<string>();
        FfmpegSmokeRunner runner = (_, args, _, _) =>
        {
            // The encoder name is the argument after "-c:v"
            var argList = args.ToList();
            int cvIdx = argList.IndexOf("-c:v");
            string enc = cvIdx >= 0 ? argList[cvIdx + 1] : "?";
            attemptLog.Add(enc);
            bool pass = enc == "h264_amf";
            return Task.FromResult(new SmokeTestRunResult(pass ? 0 : 1, false, pass ? "" : "driver error"));
        };

        FfmpegCapabilityProbeResult result = await FfmpegCapabilityProbe.RunSmokeTestAsync(
            base_, runner, CancellationToken.None);

        Assert.True(result.SmokeTestPassed);
        Assert.Equal("h264_amf", result.SmokeTestEncoder);
        Assert.NotNull(result.PreferredEncoder);
        Assert.Equal("h264_amf", result.PreferredEncoder!.Value.EncoderName);

        Assert.Equal(2, attemptLog.Count);
        Assert.Equal("h264_nvenc", attemptLog[0]);
        Assert.Equal("h264_amf", attemptLog[1]);
    }

    // When the only fallback candidate after a smoke failure is an integrated
    // backend, the smoke loop must stop -- not retry on Qsv/MediaFoundation
    // (which we refuse) and not silently report success.
    [Fact]
    public async Task SmokeTest_RefusesIntegratedFallbackWhenDiscreteFails()
    {
        var nvenc = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");
        var qsv = new HardwareEncoderCapability("h264_qsv", HardwareEncoderBackend.Qsv, "Intel QSV");
        var location = new FfmpegLocation("ffmpeg.exe", FfmpegLocationKind.Bundled);

        var base_ = new FfmpegCapabilityProbeResult(
            location,
            new FfmpegVersionInfo("ffmpeg version 7.1", "7.1"),
            new[] { nvenc, qsv },
            nvenc,
            FfmpegCapabilityProbeStatus.Ready,
            "ok");

        var attemptLog = new List<string>();
        FfmpegSmokeRunner runner = (_, args, _, _) =>
        {
            var argList = args.ToList();
            int cvIdx = argList.IndexOf("-c:v");
            string enc = cvIdx >= 0 ? argList[cvIdx + 1] : "?";
            attemptLog.Add(enc);
            return Task.FromResult(new SmokeTestRunResult(1, false, "driver error"));
        };

        FfmpegCapabilityProbeResult result = await FfmpegCapabilityProbe.RunSmokeTestAsync(
            base_, runner, CancellationToken.None);

        Assert.False(result.SmokeTestPassed);
        Assert.Null(result.SmokeTestEncoder);
        // Only nvenc was tried -- qsv was filtered out of the candidate pool
        // before the retry loop reached it.
        Assert.Single(attemptLog);
        Assert.Equal("h264_nvenc", attemptLog[0]);
    }

    [Fact]
    public async Task SmokeTest_ReturnsFailedWhenAllEncodersFail()
    {
        var nvenc = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");
        var location = new FfmpegLocation("ffmpeg.exe", FfmpegLocationKind.Bundled);

        var base_ = new FfmpegCapabilityProbeResult(
            location,
            new FfmpegVersionInfo("ffmpeg version 7.1", "7.1"),
            new[] { nvenc },
            nvenc,
            FfmpegCapabilityProbeStatus.Ready,
            "ok");

        FfmpegSmokeRunner alwaysFail = (_, _, _, _) =>
            Task.FromResult(new SmokeTestRunResult(1, false, "no driver"));

        FfmpegCapabilityProbeResult result = await FfmpegCapabilityProbe.RunSmokeTestAsync(
            base_, alwaysFail, CancellationToken.None);

        Assert.False(result.SmokeTestPassed);
        Assert.Null(result.SmokeTestEncoder);
    }

    [Fact]
    public async Task SmokeTest_PassesWhenFirstEncoderSucceeds()
    {
        var nvenc = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");
        var location = new FfmpegLocation("ffmpeg.exe", FfmpegLocationKind.Bundled);

        var base_ = new FfmpegCapabilityProbeResult(
            location,
            null,
            new[] { nvenc },
            nvenc,
            FfmpegCapabilityProbeStatus.Ready,
            "ok");

        FfmpegSmokeRunner alwaysPass = (_, _, _, _) =>
            Task.FromResult(new SmokeTestRunResult(0, false, ""));

        FfmpegCapabilityProbeResult result = await FfmpegCapabilityProbe.RunSmokeTestAsync(
            base_, alwaysPass, CancellationToken.None);

        Assert.True(result.SmokeTestPassed);
        Assert.Equal("h264_nvenc", result.SmokeTestEncoder);
    }

    [Theory]
    [InlineData("Nvenc", "h264_nvenc")]
    [InlineData("Qsv", "h264_qsv")]
    [InlineData("Amf", "h264_amf")]
    [InlineData("MediaFoundation", "h264_mf")]
    public void BuildSmokeArgs_ContainsEncoderNameAndTestsrc(string backendName, string encoderName)
    {
        var backend = Enum.Parse<HardwareEncoderBackend>(backendName);
        var encoder = new HardwareEncoderCapability(encoderName, backend, encoderName);
        IReadOnlyList<string> args = FfmpegCapabilityProbe.BuildSmokeArgs(encoder);

        Assert.Contains("-c:v", args);
        int cvIdx = args.ToList().IndexOf("-c:v");
        Assert.Equal(encoderName, args[cvIdx + 1]);
        Assert.Contains(args, a => a.Contains("testsrc", StringComparison.Ordinal));
        Assert.Contains("-f", args);
        Assert.Equal("-", args[^1]);
        Assert.Contains("-hide_banner", args);
        Assert.Contains("-nostdin", args);
    }

    [Fact]
    public void BuildSmokeArgs_NvencIncludesCudaHwaccel()
    {
        var encoder = new HardwareEncoderCapability("h264_nvenc", HardwareEncoderBackend.Nvenc, "NVIDIA NVENC");
        IReadOnlyList<string> args = FfmpegCapabilityProbe.BuildSmokeArgs(encoder);

        var argList = args.ToList();
        int hwIdx = argList.IndexOf("-hwaccel");
        Assert.True(hwIdx >= 0);
        Assert.Equal("cuda", argList[hwIdx + 1]);
    }

    [Fact]
    public async Task ProbeAsync_SkipsSmokeWhenNoEncoder()
    {
        string temp = Path.Combine(Path.GetTempPath(), "wkvrcproxy-smoke-noop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "tools"));
        File.WriteAllText(Path.Combine(temp, "tools", "ffmpeg.exe"), "");

        var smokeInvoked = false;
        FfmpegSmokeRunner neverRun = (_, _, _, _) =>
        {
            smokeInvoked = true;
            return Task.FromResult(new SmokeTestRunResult(0, false, ""));
        };

        try
        {
            // Capture returns empty encoder list -> NoHardwareEncoder -> smoke skipped
            FfmpegCapabilityProbeResult result = await FfmpegCapabilityProbe.ProbeAsync(
                temp,
                pathEnvironment: "",
                timeout: TimeSpan.FromMilliseconds(200),
                ct: CancellationToken.None,
                capture: static (_, _, _, _) => Task.FromResult("ffmpeg version 7.1\n" + ""),
                smokeRunner: neverRun);

            Assert.Equal(FfmpegCapabilityProbeStatus.NoHardwareEncoder, result.Status);
            Assert.False(smokeInvoked);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
