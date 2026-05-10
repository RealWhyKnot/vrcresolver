namespace WKVRCProxy;

internal enum TranscodeLeaseState
{
    Missing,
    Leased,
    Returned,
    Verified,
    Published,
    Expired,
    Invalid,
}

internal sealed record TranscodeLease(
    string JobId,
    string PlaybackId,
    string Rendition,
    int SegmentIndex,
    double StartPtsSeconds,
    double DurationSeconds,
    int DeadlineMilliseconds,
    string LeaseId,
    Uri InputChunkUrl,
    TranscodeOutputSpec OutputSpec);

internal sealed record TranscodeOutputSpec(
    string Codec,
    string PixelFormat,
    string Profile,
    int GopSeconds,
    string Audio);

internal sealed record TranscodeTelemetry(
    string EncoderName,
    string FfmpegVersion,
    int EncodeMilliseconds,
    int UploadMilliseconds,
    double EncodeSpeedRatio,
    bool GameImpactOk);

internal sealed record TranscodeResult(
    string LeaseId,
    bool Success,
    string OutputPath,
    long OutputBytes,
    string ErrorReason,
    TranscodeTelemetry? Telemetry)
{
    public static TranscodeResult Failed(string leaseId, string reason)
    {
        return new TranscodeResult(leaseId, false, "", 0, reason, null);
    }
}
