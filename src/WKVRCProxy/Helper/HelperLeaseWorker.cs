using System.Diagnostics;
using System.Net.Http.Headers;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal sealed record HelperLeaseRunResult(
    bool Success,
    string Status,
    string? Error,
    long Bytes,
    long ElapsedMilliseconds,
    string? Encoder,
    string? FfmpegVersion,
    // Window-pull flow only. "uploaded" when bytes reached the server,
    // "dropped" when the client discarded them per helper_drop_window or a
    // TTL expiry. Null on legacy results (treated as "uploaded" by the
    // server for telemetry purposes).
    string? Phase = null,
    // Window-pull flow only. ms between helper_window_ready dispatch and
    // the terminating pull/drop. Lets the server learn helper lead time
    // without polling.
    long? HeldMs = null);

internal static partial class HelperLeaseWorker
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public static async Task<HelperLeaseRunResult> RunAsync(
        HelperTranscodeLeaseFrame leaseFrame,
        AppSettings settings,
        string installDirectory,
        HttpClient httpClient,
        IHelperLeaseChannel channel,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? ffmpegVersion = null;
        string? encoderName = null;
        string outputPath = Path.Combine(
            Path.GetTempPath(),
            "wkvrc-helper-" + Guid.NewGuid().ToString("N") + ".ts");

        try
        {
            LogLease(leaseFrame, "received", "deadline_ms=" + leaseFrame.DeadlineMs
                + " input=" + LogUtil.RedactUrl(leaseFrame.InputUrl));

            settings = (settings ?? new AppSettings()).Normalize();
            var throttle = HelperSelfThrottle.Evaluate(settings, DefaultSignals());
            if (!throttle.CanAcceptWork)
            {
                WarnLease(leaseFrame, "rejected", throttle.State + ": " + throttle.Reason);
                return Result(false, throttle.State, throttle.Reason);
            }

            FfmpegCapabilityProbeResult probe = await FfmpegCapabilityProbe.ProbeAsync(
                installDirectory,
                ProbeTimeout,
                ct).ConfigureAwait(false);
            ffmpegVersion = probe.Version?.Version;
            if (!probe.Location.HasValue)
            {
                WarnLease(leaseFrame, "rejected", "missing_ffmpeg: " + probe.Message);
                return Result(false, "missing_ffmpeg", probe.Message);
            }
            if (!probe.PreferredEncoder.HasValue)
            {
                WarnLease(leaseFrame, "rejected", "no_encoder: " + probe.Message);
                return Result(false, "no_encoder", probe.Message);
            }

            HelperEncodingQuality quality = await HelperBenchmarkService.ResolveQualityAsync(
                settings,
                probe,
                ct).ConfigureAwait(false);
            FfmpegLocation ffmpegLocation = probe.Location.Value;
            HardwareEncoderCapability encoder = probe.PreferredEncoder.Value;
            encoderName = encoder.EncoderName;
            var lease = ToLease(leaseFrame);
            // Seg 0 skips hardware decode and uses the software-decode-into-NVENC
            // path. NVDEC's first-frame latency is large enough on an HEVC source
            // that NVENC sometimes writes the TS container header before any
            // reference frame is decoded, producing a ~10 KB output with PAT/PMT
            // and zero PES packets. Seg N>0 dodges this because `-ss N` consumes
            // some of the chunk before the output window begins, which warms the
            // decoder pipeline. There is no equivalent runway at start-of-stream,
            // so route seg 0 directly to software decode -- CPU decodes HEVC
            // frames sequentially with no GPU-context cold start, then NVENC
            // encodes them normally. Slightly slower but deterministic.
            bool softwareDecodeFirst = leaseFrame.SegmentIndex == 0;
            TranscodeFfmpegCommand command = softwareDecodeFirst
                ? TranscodeWorkerProcess.BuildSegmentCommandSoftwareFallback(
                    ffmpegLocation.Path,
                    lease,
                    encoder,
                    outputPath,
                    leaseFrame.TargetWidth,
                    leaseFrame.TargetHeight,
                    leaseFrame.TargetBitrateKbps,
                    hasAudio: leaseFrame.HasAudio,
                    quality: quality)
                : TranscodeWorkerProcess.BuildSegmentCommand(
                    ffmpegLocation.Path,
                    lease,
                    encoder,
                    outputPath,
                    leaseFrame.TargetWidth,
                    leaseFrame.TargetHeight,
                    leaseFrame.TargetBitrateKbps,
                    hasAudio: leaseFrame.HasAudio,
                    quality: quality);

            int deadlineMs = Math.Clamp(leaseFrame.DeadlineMs <= 0 ? 6000 : leaseFrame.DeadlineMs, 1000, 60000);
            LogLease(leaseFrame, "ffmpeg start", "encoder=" + encoderName
                + " decode=" + (softwareDecodeFirst ? "software_seg0" : "hardware")
                + " quality=" + HelperEncodingQualityNames.Format(quality)
                + " segment=" + leaseFrame.SegmentIndex
                + " start=" + leaseFrame.StartPts.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + " duration=" + leaseFrame.Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + " deadline_ms=" + deadlineMs);

            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(deadlineMs);
            FfmpegProcessResult ffmpegResult = await RunFfmpegAsync(command, deadlineCts.Token, ct)
                .ConfigureAwait(false);
            if (ffmpegResult.TimedOut)
            {
                WarnLease(leaseFrame, "deadline", "ffmpeg exceeded "
                    + deadlineMs + "ms stderr=" + Snip(ffmpegResult.Stderr, 180));
                return Result(false, "deadline", "helper encode exceeded the server deadline");
            }

            LogLease(leaseFrame, "ffmpeg exit", "exit_code=" + ffmpegResult.ExitCode
                + " stderr=" + Snip(ffmpegResult.Stderr, 180));

            if (ffmpegResult.ExitCode != 0 && !softwareDecodeFirst)
            {
                ConsoleUx.Warn(LogComponent.Helper, "hwaccel failed lease=" + Safe(leaseFrame.LeaseId, 64)
                    + " exit=" + ffmpegResult.ExitCode
                    + " retry=software stderr=" + Snip(ffmpegResult.Stderr, 120));
                WarnLease(leaseFrame, "ffmpeg hwaccel failed", "exit=" + ffmpegResult.ExitCode
                    + " retry=software stderr=" + Snip(ffmpegResult.Stderr, 180));
                TryDeleteOutput(outputPath);

                TranscodeFfmpegCommand fallbackCommand = TranscodeWorkerProcess.BuildSegmentCommandSoftwareFallback(
                    ffmpegLocation.Path,
                    lease,
                    encoder,
                    outputPath,
                    leaseFrame.TargetWidth,
                    leaseFrame.TargetHeight,
                    leaseFrame.TargetBitrateKbps,
                    hasAudio: leaseFrame.HasAudio,
                    quality: quality);

                LogLease(leaseFrame, "ffmpeg fallback start", "encoder=" + encoderName
                    + " quality=" + HelperEncodingQualityNames.Format(quality)
                    + " segment=" + leaseFrame.SegmentIndex
                    + " deadline_ms=" + deadlineMs);

                ffmpegResult = await RunFfmpegAsync(fallbackCommand, deadlineCts.Token, ct)
                    .ConfigureAwait(false);
                if (ffmpegResult.TimedOut)
                {
                    WarnLease(leaseFrame, "deadline", "fallback ffmpeg exceeded "
                        + deadlineMs + "ms stderr=" + Snip(ffmpegResult.Stderr, 180));
                    return Result(false, "deadline", "helper encode exceeded the server deadline");
                }
                LogLease(leaseFrame, "ffmpeg exit", "exit_code=" + ffmpegResult.ExitCode
                    + " stderr=" + Snip(ffmpegResult.Stderr, 180));
            }

            if (ffmpegResult.ExitCode != 0)
            {
                WarnLease(leaseFrame, "ffmpeg failed", "fallback_exit=" + ffmpegResult.ExitCode
                    + " stderr=" + Snip(ffmpegResult.Stderr, 180));
                return Result(false, "ffmpeg_failed", Snip(ffmpegResult.Stderr, 240));
            }

            var info = new FileInfo(outputPath);
            if (!info.Exists || info.Length <= 0)
            {
                WarnLease(leaseFrame, "empty output", "ffmpeg completed without a segment");
                return Result(false, "empty_output", "ffmpeg completed without a segment");
            }

            string? validationError = ValidateMpegTs(outputPath, info.Length);
            if (validationError != null)
            {
                WarnLease(leaseFrame, "local_validation_failed", validationError
                    + " bytes=" + info.Length);
                return Result(false, "local_validation_failed", validationError);
            }

            // Post-encode video-duration check. NVDEC reference-pool starvation
            // on HEVC sources produces a TS that's structurally valid -- sync
            // bytes plus PES packets plus full-length audio -- but where the
            // encoder dropped most of the video frames mid-segment, leaving
            // ~0.7-1.0 s of video out of the 4 s window. Players freeze when
            // the video stream ends mid-segment. The local validator can't
            // catch this without probing duration.
            //
            // Run the check for every segment regardless of decode path:
            //   * hardware (decode != software_seg0): if truncated, retry
            //     once with software_seg0 before uploading.
            //   * software_seg0 (seg 0 special-cased): if STILL truncated,
            //     the source itself is missing reference frames at t=0
            //     (Tubi catalog: HEVC POC chain references frames not in
            //     the chunk). Fail locally so the server uses peer/CPU for
            //     this segment instead of paying the upload + 422 cost.
            if (leaseFrame.Duration > 0)
            {
                double? vidDuration = await ProbeVideoDurationAsync(ffmpegLocation.Path, outputPath, deadlineCts.Token).ConfigureAwait(false);
                if (vidDuration.HasValue && vidDuration.Value < leaseFrame.Duration * 0.8)
                {
                    string vidStr = vidDuration.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    string expStr = leaseFrame.Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    if (softwareDecodeFirst)
                    {
                        // Already on software; nothing left to try. Fail the
                        // lease locally with a clean reason so the server
                        // routes to peer / CPU for this segment.
                        WarnLease(leaseFrame, "truncated_video", "video_dur=" + vidStr
                            + " expected=" + expStr + " bytes=" + info.Length
                            + " path=software_seg0 no_retry_available");
                        TryDeleteOutput(outputPath);
                        return Result(false, "truncated_video",
                            "software decode produced video_dur=" + vidStr + " of expected " + expStr);
                    }

                    WarnLease(leaseFrame, "truncated_video", "video_dur=" + vidStr
                        + " expected=" + expStr + " bytes=" + info.Length + " retry=software");
                    TryDeleteOutput(outputPath);

                    TranscodeFfmpegCommand swCommand = TranscodeWorkerProcess.BuildSegmentCommandSoftwareFallback(
                        ffmpegLocation.Path,
                        lease,
                        encoder,
                        outputPath,
                        leaseFrame.TargetWidth,
                        leaseFrame.TargetHeight,
                        leaseFrame.TargetBitrateKbps,
                        hasAudio: leaseFrame.HasAudio,
                        quality: quality);

                    LogLease(leaseFrame, "ffmpeg sw-retry start", "reason=truncated_video deadline_ms=" + deadlineMs);
                    ffmpegResult = await RunFfmpegAsync(swCommand, deadlineCts.Token, ct).ConfigureAwait(false);
                    if (ffmpegResult.TimedOut)
                    {
                        WarnLease(leaseFrame, "deadline", "sw-retry ffmpeg exceeded "
                            + deadlineMs + "ms stderr=" + Snip(ffmpegResult.Stderr, 180));
                        return Result(false, "deadline", "helper sw-retry exceeded the server deadline");
                    }
                    if (ffmpegResult.ExitCode != 0)
                    {
                        WarnLease(leaseFrame, "ffmpeg sw-retry failed", "exit=" + ffmpegResult.ExitCode
                            + " stderr=" + Snip(ffmpegResult.Stderr, 180));
                        return Result(false, "ffmpeg_failed", Snip(ffmpegResult.Stderr, 240));
                    }
                    info = new FileInfo(outputPath);
                    if (!info.Exists || info.Length <= 0)
                    {
                        WarnLease(leaseFrame, "empty sw-retry output", "ffmpeg sw-retry completed without a segment");
                        return Result(false, "empty_output", "ffmpeg sw-retry completed without a segment");
                    }
                    validationError = ValidateMpegTs(outputPath, info.Length);
                    if (validationError != null)
                    {
                        WarnLease(leaseFrame, "sw-retry local_validation_failed", validationError + " bytes=" + info.Length);
                        return Result(false, "local_validation_failed", validationError);
                    }
                    // Re-probe duration after the software retry. If software
                    // ALSO produced truncated output (source-level structural
                    // issue), fail locally rather than upload a known-bad seg.
                    double? swVidDuration = await ProbeVideoDurationAsync(ffmpegLocation.Path, outputPath, deadlineCts.Token).ConfigureAwait(false);
                    if (swVidDuration.HasValue && swVidDuration.Value < leaseFrame.Duration * 0.8)
                    {
                        string swVidStr = swVidDuration.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        WarnLease(leaseFrame, "truncated_video", "sw-retry video_dur=" + swVidStr
                            + " expected=" + expStr + " bytes=" + info.Length + " no_retry_available");
                        TryDeleteOutput(outputPath);
                        return Result(false, "truncated_video",
                            "sw retry still produced video_dur=" + swVidStr + " of expected " + expStr);
                    }
                    LogLease(leaseFrame, "ffmpeg sw-retry ok", "bytes=" + info.Length
                        + " video_dur=" + (swVidDuration.HasValue ? swVidDuration.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "?"));
                }
            }

            // Window-pull flow: the lease frame carries HeldTtlMs AND the
            // server advertised the helper_window_pull feature AND this
            // client opted in via helper_status.supports_window_pull. The
            // worker announces metrics, holds bytes in the hold directory,
            // and waits for the server to pull or drop. HeldTtlMs is the
            // explicit opt-in signal -- legacy leases omit it entirely.
            //
            // SegmentCount is informational on the wire; for now the helper
            // still produces ONE segment per lease regardless of SegmentCount
            // (server batches per-segment leases for its scheduler state).
            bool windowPullFlow = leaseFrame.HeldTtlMs.HasValue && channel.WindowPullEnabled;
            if (windowPullFlow)
            {
                return await HoldAndAwaitAsync(
                    leaseFrame,
                    channel,
                    httpClient,
                    outputPath,
                    info.Length,
                    encoderName,
                    ffmpegVersion,
                    ffmpegLocation.Path,
                    deadlineCts.Token,
                    sw).ConfigureAwait(false);
            }

            string uploadUrlHost = ExtractUrlHost(leaseFrame.UploadUrl);
            LogLease(leaseFrame, "upload start", "upload_url_host=" + uploadUrlHost
                + " bytes=" + info.Length);
            UploadResult uploadResult = await UploadAsync(httpClient, leaseFrame.UploadUrl, outputPath, deadlineCts.Token).ConfigureAwait(false);
            LogLease(leaseFrame, "upload end", "http_status=" + uploadResult.HttpStatus
                + " bytes=" + uploadResult.Bytes
                + " elapsed_ms=" + uploadResult.ElapsedMs);
            return Result(true, "uploaded", null, info.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            WarnLease(leaseFrame, "deadline", "lease exceeded deadline during upload");
            return Result(false, "deadline", "helper lease exceeded the server deadline");
        }
        catch (Exception ex)
        {
            WarnLease(leaseFrame, "error", ex.GetType().Name + ": " + ex.Message);
            return Result(false, "error", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }

        HelperLeaseRunResult Result(bool success, string status, string? error, long bytes = 0)
        {
            return new HelperLeaseRunResult(
                success,
                status,
                error,
                bytes,
                sw.ElapsedMilliseconds,
                encoderName,
                ffmpegVersion);
        }
    }

    private static TranscodeLease ToLease(HelperTranscodeLeaseFrame frame)
    {
        return new TranscodeLease(
            JobId: frame.PlaybackId,
            PlaybackId: frame.PlaybackId,
            Rendition: frame.Rendition,
            SegmentIndex: frame.SegmentIndex,
            StartPtsSeconds: frame.StartPts,
            DurationSeconds: frame.Duration,
            DeadlineMilliseconds: frame.DeadlineMs,
            LeaseId: frame.LeaseId,
            InputChunkUrl: new Uri(frame.InputUrl),
            OutputSpec: new TranscodeOutputSpec(
                Codec: frame.OutputSpec.Codec,
                PixelFormat: frame.OutputSpec.PixelFormat,
                Profile: frame.OutputSpec.Profile,
                GopSeconds: frame.OutputSpec.GopSeconds,
                Audio: frame.OutputSpec.Audio));
    }

    // Window-pull-flow holding store. Lease bytes are moved here after
    // transcode+validation and held until the server pulls or drops them.
    // Lifetime is bounded by the lease's HeldTtlMs (or the default below) so
    // a crashed agent never leaves bytes on disk longer than ~1 minute. The
    // path is under the system temp dir so existing temp-cleaner policies
    // catch any stragglers across reboots.
    private static readonly string HoldDirectory =
        Path.Combine(Path.GetTempPath(), "wkvrc-helper-hold");
    private static readonly TimeSpan HoldSweepThreshold = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultHoldTtl = TimeSpan.FromSeconds(8);
    private static int s_holdSweepGate;

    private sealed record UploadResult(int HttpStatus, long Bytes, long ElapsedMs);

    private sealed record FfmpegProcessResult(bool TimedOut, int ExitCode, string Stderr);

}
