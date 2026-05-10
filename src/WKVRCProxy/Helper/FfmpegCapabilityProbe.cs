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

internal sealed record FfmpegCapabilityProbeResult(
    FfmpegLocation? Location,
    FfmpegVersionInfo? Version,
    IReadOnlyList<HardwareEncoderCapability> Encoders,
    HardwareEncoderCapability? PreferredEncoder,
    FfmpegCapabilityProbeStatus Status,
    string Message)
{
    public bool HasFfmpeg => Location.HasValue;
    public bool CanUseHardwareH264 => Status == FfmpegCapabilityProbeStatus.Ready && PreferredEncoder.HasValue;
}

internal static class FfmpegCapabilityProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

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
        FfmpegProbeCapture? capture = null)
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

            return FromOutputs(location.Value, versionOutput, encoderOutput);
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

    public static FfmpegCapabilityProbeResult FromOutputs(
        FfmpegLocation location,
        string versionOutput,
        string encoderOutput)
    {
        FfmpegVersionInfo? version = FfmpegVersionProbe.ParseVersion(versionOutput);
        IReadOnlyList<HardwareEncoderCapability> encoders = HardwareEncoderProbe.ParseEncoders(encoderOutput);
        HardwareEncoderCapability? preferred = HardwareEncoderProbe.ChoosePreferred(encoders);

        if (!preferred.HasValue)
        {
            return new FfmpegCapabilityProbeResult(
                location,
                version,
                encoders,
                null,
                FfmpegCapabilityProbeStatus.NoHardwareEncoder,
                "FFmpeg is installed, but no supported H.264 hardware encoder was listed.");
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
