using System.Diagnostics;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal enum FfmpegCapabilityProbeStatus
{
    NotFound,
    Ready,
    NoHardwareEncoder,
    TimedOut,
    Failed,
}

internal delegate Task<string> FfmpegProbeCapture(
    string ffmpegPath,
    string argument,
    TimeSpan timeout,
    CancellationToken ct);

// Delegate for running the smoke-test subprocess; injectable for unit tests.
internal delegate Task<SmokeTestRunResult> FfmpegSmokeRunner(
    string ffmpegPath,
    IReadOnlyList<string> args,
    TimeSpan timeout,
    CancellationToken ct);

internal readonly record struct SmokeTestRunResult(int ExitCode, bool TimedOut, string Stderr);

internal sealed record FfmpegCapabilityProbeResult(
    FfmpegLocation? Location,
    FfmpegVersionInfo? Version,
    IReadOnlyList<HardwareEncoderCapability> Encoders,
    HardwareEncoderCapability? PreferredEncoder,
    FfmpegCapabilityProbeStatus Status,
    string Message,
    bool SmokeTestPassed = false,
    string? SmokeTestEncoder = null)
{
    public bool HasFfmpeg => Location.HasValue;
    public bool CanUseHardwareH264 => Status == FfmpegCapabilityProbeStatus.Ready && PreferredEncoder.HasValue;
}

internal static class FfmpegCapabilityProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(3);

    public static Task<FfmpegCapabilityProbeResult> ProbeAsync(
        string installDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        return ProbeAsync(installDirectory, null, timeout, ct);
    }

    public static async Task<FfmpegCapabilityProbeResult> ProbeAsync(
        string installDirectory,
        string? pathEnvironment,
        TimeSpan timeout,
        CancellationToken ct,
        FfmpegProbeCapture? capture = null,
        FfmpegSmokeRunner? smokeRunner = null)
    {
        FfmpegLocation? location = FfmpegLocator.Locate(installDirectory, pathEnvironment);
        if (!location.HasValue)
        {
            return new FfmpegCapabilityProbeResult(
                null,
                null,
                Array.Empty<HardwareEncoderCapability>(),
                null,
                FfmpegCapabilityProbeStatus.NotFound,
                "FFmpeg was not found in tools or PATH.");
        }

        capture ??= FfmpegVersionProbe.CaptureAsync;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            TimeSpan effectiveTimeout = timeout <= TimeSpan.Zero ? DefaultTimeout : timeout;
            timeoutCts.CancelAfter(effectiveTimeout);

            string versionOutput = await capture(location.Value.Path, "-version", effectiveTimeout, timeoutCts.Token)
                .ConfigureAwait(false);
            string encoderOutput = await capture(location.Value.Path, "-encoders", effectiveTimeout, timeoutCts.Token)
                .ConfigureAwait(false);

            var baseResult = FromOutputs(location.Value, versionOutput, encoderOutput);
            if (!baseResult.HasFfmpeg || baseResult.Status == FfmpegCapabilityProbeStatus.NoHardwareEncoder)
                return baseResult;

            return await RunSmokeTestAsync(baseResult, smokeRunner, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FfmpegCapabilityProbeResult(
                location,
                null,
                Array.Empty<HardwareEncoderCapability>(),
                null,
                FfmpegCapabilityProbeStatus.TimedOut,
                "FFmpeg capability probe timed out.");
        }
        catch (Exception ex)
        {
            return new FfmpegCapabilityProbeResult(
                location,
                null,
                Array.Empty<HardwareEncoderCapability>(),
                null,
                FfmpegCapabilityProbeStatus.Failed,
                "FFmpeg capability probe failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // Run a ~0.5 s smoke encode with the chosen encoder. On failure, demote
    // that encoder and re-run ChoosePreferred against the remainder.
    // Returns the result with SmokeTestPassed / SmokeTestEncoder populated.
    internal static async Task<FfmpegCapabilityProbeResult> RunSmokeTestAsync(
        FfmpegCapabilityProbeResult baseResult,
        FfmpegSmokeRunner? smokeRunner,
        CancellationToken ct)
    {
        smokeRunner ??= DefaultSmokeRunner;

        if (!baseResult.Location.HasValue || !baseResult.PreferredEncoder.HasValue)
            return baseResult;

        string ffmpegPath = baseResult.Location.Value.Path;
        var remaining = new List<HardwareEncoderCapability>(baseResult.Encoders);

        while (remaining.Count > 0)
        {
            HardwareEncoderCapability? candidate = HardwareEncoderProbe.ChoosePreferred(remaining);
            if (!candidate.HasValue) break;

            var smokeArgs = BuildSmokeArgs(candidate.Value);
            var sw = Stopwatch.StartNew();
            SmokeTestRunResult smokeResult;
            try
            {
                smokeResult = await smokeRunner(ffmpegPath, smokeArgs, SmokeTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                smokeResult = new SmokeTestRunResult(-1, false, ex.GetType().Name + ": " + ex.Message);
            }
            sw.Stop();

            bool passed = !smokeResult.TimedOut && smokeResult.ExitCode == 0;
            string stderrSnip = smokeResult.Stderr.Length > 160
                ? smokeResult.Stderr[..157] + "..."
                : smokeResult.Stderr;

            Logger.WriteDiagnostic(
                LogComponent.Helper,
                "[helper][probe] helper_encoder_smoke_test encoder=" + candidate.Value.EncoderName
                    + " passed=" + passed
                    + " exit_code=" + smokeResult.ExitCode
                    + " elapsed_ms=" + sw.ElapsedMilliseconds
                    + (passed ? "" : " stderr_snip=" + stderrSnip),
                "helper_encoder_smoke_test encoder=" + candidate.Value.EncoderName
                    + " passed=" + passed
                    + " exit_code=" + smokeResult.ExitCode
                    + " elapsed_ms=" + sw.ElapsedMilliseconds
                    + (passed ? "" : " stderr_snip=" + stderrSnip));

            if (passed)
            {
                return baseResult with
                {
                    PreferredEncoder = candidate,
                    SmokeTestPassed = true,
                    SmokeTestEncoder = candidate.Value.EncoderName,
                };
            }

            // Demote and try the next preference.
            remaining.RemoveAll(e => e.EncoderName == candidate.Value.EncoderName);
        }

        // All candidates failed smoke.
        return baseResult with
        {
            SmokeTestPassed = false,
            SmokeTestEncoder = null,
        };
    }

    // Build the smoke-encode argument list: testsrc -> chosen encoder -> null muxer.
    // Uses the same hwaccel flags pattern as TranscodeWorkerProcess to catch
    // driver-level failures that -encoders listing alone won't expose.
    internal static IReadOnlyList<string> BuildSmokeArgs(HardwareEncoderCapability encoder)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-loglevel",
            "error",
        };

        // Hardware decode flags (same pattern as TranscodeWorkerProcess.HardwareDecodeOptionsFor)
        switch (encoder.Backend)
        {
            case HardwareEncoderBackend.Nvenc:
                args.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
                break;
            case HardwareEncoderBackend.Qsv:
                args.AddRange(new[] { "-hwaccel", "qsv", "-hwaccel_output_format", "qsv" });
                break;
            case HardwareEncoderBackend.Amf:
            case HardwareEncoderBackend.MediaFoundation:
                args.AddRange(new[] { "-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11" });
                break;
        }

        args.AddRange(new[]
        {
            "-f", "lavfi",
            "-i", "testsrc=duration=0.5:size=320x240:rate=30",
            "-c:v", encoder.EncoderName,
            "-f", "null",
            "-",
        });

        return args;
    }

    private static async Task<SmokeTestRunResult> DefaultSmokeRunner(
        string ffmpegPath,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            string partial = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
            return new SmokeTestRunResult(-1, true, partial);
        }

        string stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);
        return new SmokeTestRunResult(process.ExitCode, false, stderr);
    }

    public static FfmpegCapabilityProbeResult FromOutputs(
        FfmpegLocation location,
        string versionOutput,
        string encoderOutput)
    {
        FfmpegVersionInfo? version = FfmpegVersionProbe.ParseVersion(versionOutput);
        IReadOnlyList<HardwareEncoderCapability> encoders = HardwareEncoderProbe.ParseEncoders(encoderOutput);
        HardwareEncoderCapability? preferred = HardwareEncoderProbe.ChoosePreferred(encoders);

        var candidateNames = new string[encoders.Count];
        for (int i = 0; i < encoders.Count; i++) candidateNames[i] = encoders[i].EncoderName;
        string candidates = encoders.Count > 0 ? string.Join(",", candidateNames) : "<none>";
        string chosen = preferred.HasValue ? preferred.Value.EncoderName : "<none>";
        Logger.WriteDiagnostic(
            LogComponent.Helper,
            "[helper][probe] helper_encoder_preference candidates=" + candidates + " chosen=" + chosen,
            "helper_encoder_preference candidates=" + candidates + " chosen=" + chosen);

        var refusedNames = new List<string>(encoders.Count);
        for (int i = 0; i < encoders.Count; i++)
        {
            if (!HardwareEncoderProbe.IsDedicatedGpuBackend(encoders[i].Backend))
                refusedNames.Add(encoders[i].EncoderName);
        }
        if (refusedNames.Count > 0)
        {
            string refused = string.Join(",", refusedNames);
            Logger.WriteDiagnostic(
                LogComponent.Helper,
                "[helper][probe] helper_encoder_refused encoders=" + refused
                    + " reason=integrated_gpu_unsupported",
                "helper_encoder_refused encoders=" + refused
                    + " reason=integrated_gpu_unsupported");
        }

        if (!preferred.HasValue)
        {
            // Distinguish "no encoders found at all" from "only integrated
            // encoders were found and refused" so the operator gets a
            // useful error rather than a generic 'not listed' line.
            string message = encoders.Count == 0
                ? "FFmpeg is installed, but no supported H.264 hardware encoder was listed."
                : "FFmpeg is installed, but only integrated-GPU encoders were available ("
                    + candidates + "); the helper requires a discrete NVIDIA or AMD GPU.";
            return new FfmpegCapabilityProbeResult(
                location,
                version,
                encoders,
                null,
                FfmpegCapabilityProbeStatus.NoHardwareEncoder,
                message);
        }

        return new FfmpegCapabilityProbeResult(
            location,
            version,
            encoders,
            preferred,
            FfmpegCapabilityProbeStatus.Ready,
            "Hardware H.264 encoding is available.");
    }
}
