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

internal static class HelperLeaseWorker
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

    // Best-effort sweep of the hold directory. Deletes any file whose mtime
    // is older than HoldSweepThreshold. Runs at most once per minute via a
    // CAS gate so a burst of incoming leases doesn't repeatedly stat the
    // whole directory. Silent on errors -- this is GC, not load-bearing.
    private static void TrySweepHoldDirectory()
    {
        if (Interlocked.Exchange(ref s_holdSweepGate, 1) == 1) return;
        try
        {
            if (!Directory.Exists(HoldDirectory)) return;
            DateTime cutoff = DateTime.UtcNow - HoldSweepThreshold;
            foreach (string path in Directory.EnumerateFiles(HoldDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                        File.Delete(path);
                }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
        finally
        {
            // Park the gate at 1 for ~1 min so we don't re-sweep on every
            // lease; the worker explicitly resets it after a wall-clock delay.
            _ = Task.Delay(HoldSweepThreshold).ContinueWith(_ => Interlocked.Exchange(ref s_holdSweepGate, 0));
        }
    }

    // Window-pull flow: bytes have been transcoded and validated. Move them
    // to the hold store, announce metrics, and park until the server pulls
    // or drops. Returns the terminal result; the held file is always cleaned
    // up before this method returns.
    private static async Task<HelperLeaseRunResult> HoldAndAwaitAsync(
        HelperTranscodeLeaseFrame leaseFrame,
        IHelperLeaseChannel channel,
        HttpClient httpClient,
        string transcodeOutputPath,
        long bytes,
        string? encoderName,
        string? ffmpegVersion,
        string ffmpegBinaryPath,
        CancellationToken ct,
        Stopwatch wallClock)
    {
        // Audio + video duration probes feed the server's metrics validator.
        // A null probe ships as zero on the wire; the validator rejects zero
        // when the lease expected the stream, so the lease drops cleanly
        // back to server-CPU rather than uploading a sized-but-mute output.
        // A warn line surfaces a probe regression before it shows up as a
        // wall of validator rejections.
        double videoDuration = 0;
        if (leaseFrame.Duration > 0)
        {
            double? probed = await ProbeVideoDurationAsync(
                ffmpegBinaryPath, transcodeOutputPath, ct).ConfigureAwait(false);
            if (!probed.HasValue)
                WarnLease(leaseFrame, "probe_failed", "stream=video output=" + transcodeOutputPath);
            videoDuration = probed ?? 0;
        }
        double audioDuration = 0;
        if (leaseFrame.HasAudio)
        {
            double? probed = await ProbeAudioDurationAsync(
                ffmpegBinaryPath, transcodeOutputPath, ct).ConfigureAwait(false);
            if (!probed.HasValue)
                WarnLease(leaseFrame, "probe_failed", "stream=audio output=" + transcodeOutputPath);
            audioDuration = probed ?? 0;
        }

        try { Directory.CreateDirectory(HoldDirectory); } catch { /* best-effort */ }
        TrySweepHoldDirectory();

        string holdPath = Path.Combine(HoldDirectory, leaseFrame.LeaseId + ".ts");
        try
        {
            if (File.Exists(holdPath)) File.Delete(holdPath);
            File.Move(transcodeOutputPath, holdPath);
        }
        catch (Exception ex)
        {
            // Move failed -- either cross-volume issue or the transcode
            // output is locked. Fall back to upload-from-temp by copying
            // bytes to hold then deleting source.
            try
            {
                File.Copy(transcodeOutputPath, holdPath, overwrite: true);
                try { File.Delete(transcodeOutputPath); } catch { /* best-effort */ }
            }
            catch
            {
                WarnLease(leaseFrame, "hold_move_failed", ex.GetType().Name + ": " + ex.Message);
                // Cannot hold -- give up on the window-pull dance and let
                // the upper finally clean up the temp file.
                return new HelperLeaseRunResult(
                    false, "hold_move_failed", ex.Message,
                    bytes, wallClock.ElapsedMilliseconds, encoderName, ffmpegVersion);
            }
        }

        var readyFrame = new HelperWindowReadyFrame
        {
            LeaseId = leaseFrame.LeaseId,
            PlaybackId = leaseFrame.PlaybackId,
            WindowStart = leaseFrame.SegmentIndex,
            SegmentCount = leaseFrame.SegmentCount ?? 1,
            Bytes = bytes,
            VideoDuration = videoDuration,
            AudioDuration = audioDuration,
            Encoder = encoderName ?? "",
            FfmpegVersion = ffmpegVersion ?? "",
            ElapsedMs = wallClock.ElapsedMilliseconds,
        };

        TimeSpan ttl = leaseFrame.HeldTtlMs is int ttlMs && ttlMs > 0
            ? TimeSpan.FromMilliseconds(Math.Clamp(ttlMs, 1000, 30000))
            : DefaultHoldTtl;

        long announceTickMs;
        try
        {
            announceTickMs = wallClock.ElapsedMilliseconds;
            await channel.SendWindowReadyAsync(readyFrame, ct).ConfigureAwait(false);
            LogLease(leaseFrame, "window_ready_sent",
                "bytes=" + bytes
                + " video_dur=" + videoDuration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + " audio_dur=" + audioDuration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + " ttl_ms=" + (int)ttl.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            WarnLease(leaseFrame, "window_ready_send_failed", ex.GetType().Name + ": " + ex.Message);
            TryDeleteHold(holdPath);
            return new HelperLeaseRunResult(
                false, "window_ready_send_failed", ex.Message,
                bytes, wallClock.ElapsedMilliseconds, encoderName, ffmpegVersion);
        }

        HelperWindowResolution resolution;
        try
        {
            resolution = await channel.WaitForWindowResolutionAsync(
                leaseFrame.LeaseId, ttl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WarnLease(leaseFrame, "window_wait_failed", ex.GetType().Name + ": " + ex.Message);
            TryDeleteHold(holdPath);
            return new HelperLeaseRunResult(
                false, "window_wait_failed", ex.Message,
                bytes, wallClock.ElapsedMilliseconds, encoderName, ffmpegVersion);
        }

        long heldMs = wallClock.ElapsedMilliseconds - announceTickMs;

        if (resolution.Outcome == HelperWindowOutcome.Pull)
        {
            string uploadUrl = string.IsNullOrEmpty(resolution.UploadUrlOverride)
                ? leaseFrame.UploadUrl
                : resolution.UploadUrlOverride!;
            string uploadHost = ExtractUrlHost(uploadUrl);
            LogLease(leaseFrame, "pull_received",
                "upload_url_host=" + uploadHost + " held_ms=" + heldMs);

            try
            {
                UploadResult upRes = await UploadAsync(httpClient, uploadUrl, holdPath, ct).ConfigureAwait(false);
                LogLease(leaseFrame, "upload end",
                    "http_status=" + upRes.HttpStatus
                    + " bytes=" + upRes.Bytes
                    + " elapsed_ms=" + upRes.ElapsedMs
                    + " held_ms=" + heldMs);
                return new HelperLeaseRunResult(
                    true, "uploaded", null,
                    bytes, wallClock.ElapsedMilliseconds, encoderName, ffmpegVersion,
                    Phase: WireConstants.HelperPhaseUploaded,
                    HeldMs: heldMs);
            }
            catch (Exception ex)
            {
                WarnLease(leaseFrame, "upload_failed", ex.GetType().Name + ": " + ex.Message);
                return new HelperLeaseRunResult(
                    false, "upload_failed", ex.Message,
                    bytes, wallClock.ElapsedMilliseconds, encoderName, ffmpegVersion,
                    Phase: WireConstants.HelperPhaseUploaded,
                    HeldMs: heldMs);
            }
            finally
            {
                TryDeleteHold(holdPath);
            }
        }

        // Drop or TtlExpired -- delete the file, return a phase=dropped
        // result so the server's terminal accounting stays accurate.
        string dropReason = resolution.DropReason ?? WireConstants.HelperDropReasonSuperseded;
        LogLease(leaseFrame, "drop_received", "reason=" + dropReason + " held_ms=" + heldMs);
        TryDeleteHold(holdPath);
        return new HelperLeaseRunResult(
            true, dropReason, null,
            bytes, wallClock.ElapsedMilliseconds, encoderName, ffmpegVersion,
            Phase: WireConstants.HelperPhaseDropped,
            HeldMs: heldMs);
    }

    private static void TryDeleteHold(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    // Probe the audio stream's duration via ffprobe. Returns null on any
    // failure (no audio stream, ffprobe missing, parse error). Symmetric
    // with ProbeVideoDurationAsync; the server's metrics validator uses the
    // pair to apply the truncated-video rule (video < audio/2).
    private static Task<double?> ProbeAudioDurationAsync(
        string ffmpegPath,
        string outputPath,
        CancellationToken ct)
        => ProbeStreamDurationAsync(ffmpegPath, outputPath, "a:0", ct);

    private sealed record UploadResult(int HttpStatus, long Bytes, long ElapsedMs);

    private static async Task<UploadResult> UploadAsync(HttpClient httpClient, string uploadUrl, string outputPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uploadUrl))
            throw new InvalidOperationException("lease did not include an upload URL");

        var info = new FileInfo(outputPath);
        long bytes = info.Exists ? info.Length : 0;
        var swUp = System.Diagnostics.Stopwatch.StartNew();
        await using var stream = File.OpenRead(outputPath);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp2t");
        using HttpResponseMessage response = await httpClient.PutAsync(uploadUrl, content, ct).ConfigureAwait(false);
        swUp.Stop();
        int status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("upload failed with HTTP " + status);
        return new UploadResult(status, bytes, swUp.ElapsedMilliseconds);
    }

    private static HelperRuntimeSignals DefaultSignals()
    {
        return new HelperRuntimeSignals(
            OnBattery: false,
            VrChatRunning: true,
            GpuBusyPercent: 0,
            CpuBusyPercent: 0,
            ThermalHeadroomPercent: 100,
            UploadQueueBytes: 0,
            ConsecutiveFailures: 0);
    }

    private sealed record FfmpegProcessResult(bool TimedOut, int ExitCode, string Stderr);

    // Probe the video stream's duration via ffprobe. Returns null when the
    // probe can't be run, the binary is missing, or the file has no video
    // stream. The caller treats null as zero in the announced metrics; the
    // server's validator rejects zero so the lease falls back to server CPU.
    private static Task<double?> ProbeVideoDurationAsync(
        string ffmpegPath,
        string outputPath,
        CancellationToken ct)
        => ProbeStreamDurationAsync(ffmpegPath, outputPath, "v:0", ct);

    // Tries stream-level duration first (per-stream tag set by the muxer)
    // and falls back to container-level duration only when the requested
    // stream actually exists. The mpegts muxer used by the helper's encode
    // pipeline does not always tag per-stream duration on a transcoded
    // output -- we observed empty stream=duration on every helper-produced
    // TS file during 2026-05-22 Tubi playback while the server's identical
    // re-encode tagged it fine, so the bare stream-level probe silently
    // reported zero. The fallback uses a stream-existence check before
    // returning the container duration so that probing an absent stream
    // (e.g. audio probe on a video-only TS) still returns null instead of
    // mislabelling the video length as audio length.
    internal static async Task<double?> ProbeStreamDurationAsync(
        string ffmpegPath,
        string outputPath,
        string streamSelector,
        CancellationToken ct)
    {
        string? ffprobePath = TryResolveFfprobePath(ffmpegPath);
        if (string.IsNullOrEmpty(ffprobePath)) return null;

        // One ffprobe call asks for index AND duration on the selected
        // stream. Output shapes:
        //   ""           -> no matching stream; return null
        //   "0"          -> stream exists, no duration tag; format fallback
        //   "0,4.012"    -> stream-level duration available
        //   "0,N/A"      -> stream exists, ffprobe couldn't read duration
        string? streamLine = await RunFfprobeAsync(
            ffprobePath,
            new[] { "-v", "error", "-show_entries", "stream=index,duration",
                    "-select_streams", streamSelector, "-of", "csv=p=0", outputPath },
            ct).ConfigureAwait(false);
        if (streamLine == null) return null;
        if (string.IsNullOrWhiteSpace(streamLine)) return null;

        string[] parts = streamLine.Split(',', 2);
        if (parts.Length >= 2
            && double.TryParse(parts[1].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double streamSecs)
            && streamSecs > 0)
        {
            return streamSecs;
        }

        string? containerLine = await RunFfprobeAsync(
            ffprobePath,
            new[] { "-v", "error", "-show_entries", "format=duration",
                    "-of", "csv=p=0", outputPath },
            ct).ConfigureAwait(false);
        if (containerLine == null) return null;
        if (double.TryParse(containerLine.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double containerSecs)
            && containerSecs > 0)
        {
            return containerSecs;
        }
        return null;
    }

    // Returns stdout (trimmed) on a clean exit, or null on launch / non-zero
    // exit / cancellation. An empty-but-clean exit returns the empty string.
    private static async Task<string?> RunFfprobeAsync(
        string ffprobePath,
        string[] args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        try
        {
            using var p = new Process { StartInfo = psi };
            p.Start();
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0) return null;
            return (await stdoutTask.ConfigureAwait(false)).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveFfprobePath(string ffmpegPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(ffmpegPath);
            if (string.IsNullOrEmpty(dir)) return null;
            string probe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            return File.Exists(probe) ? probe : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<FfmpegProcessResult> RunFfmpegAsync(
        TranscodeFfmpegCommand command,
        CancellationToken deadlineToken,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = command.ToStartInfo() };
        process.Start();
        TranscodeWorkerProcess.ApplySafePriority(process);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new FfmpegProcessResult(true, -1, CompletedTaskText(stderrTask, 180));
        }

        string stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);
        return new FfmpegProcessResult(false, process.ExitCode, stderr);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static void TryDeleteOutput(string outputPath)
    {
        try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
    }

    private static string Snip(string value, int max)
    {
        value = (value ?? "").Trim();
        if (value.Length <= max) return value;
        return value[..Math.Max(0, max - 2)] + "..";
    }

    private static void LogLease(HelperTranscodeLeaseFrame lease, string stage, string detail)
    {
        string fileLine = "[mesh][helper] lease " + stage + " lease=" + Safe(lease.LeaseId, 64)
            + " stream=" + Safe(lease.PlaybackId, 64)
            + " segment=" + lease.SegmentIndex
            + " " + detail;
        Logger.WriteDiagnostic(LogComponent.Helper, fileLine,
            "lease " + stage + " segment=" + lease.SegmentIndex + " " + detail);
    }

    private static void WarnLease(HelperTranscodeLeaseFrame lease, string stage, string detail)
    {
        string fileLine = "[mesh][helper][warn] lease " + stage + " lease=" + Safe(lease.LeaseId, 64)
            + " stream=" + Safe(lease.PlaybackId, 64)
            + " segment=" + lease.SegmentIndex
            + " " + detail;
        Logger.WarnDiagnostic(LogComponent.Helper, fileLine,
            "lease " + stage + " segment=" + lease.SegmentIndex + " " + detail);
    }

    private static string CompletedTaskText(Task<string> task, int max)
    {
        if (task.IsCompletedSuccessfully)
            return Snip(task.Result, max);
        return "";
    }

    private static string Safe(string value, int max)
        => LogUtil.SanitizeForConsole(value ?? "", max);

    // Check sync byte 0x47 at offsets 0, 188, 376 -- mirrors the server-side
    // Pre-upload sanity check on the locally-produced TS segment. Returns null on
    // success or a short error string on failure. Mirrors WhyKnotDev's
    // MpegTsValidator gates so bad output is caught locally before an upload
    // roundtrip and a server-side 422.
    //
    // Layers:
    //   1. file size minimum (need at least 3 packets for the sync probe)
    //   2. 0x47 sync byte at offsets 0, 188, 376
    //   3. PES start codes -- at least one PUSI packet whose payload begins
    //      with 0x000001. A file that passes layers 1-2 but emits only PAT/PMT
    //      sections is the cold-encoder failure mode where NVENC writes the
    //      container header before any frame has been decoded.
    internal static string? ValidateMpegTs(string path, long fileLength)
    {
        if (fileLength < 564)
            return "file_too_small bytes=" + fileLength;

        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 564))
            {
                Span<byte> buf = stackalloc byte[564];
                int read = fs.Read(buf);
                if (read < 564)
                    return "short_read read=" + read;

                if (buf[0] != 0x47 || buf[188] != 0x47 || buf[376] != 0x47)
                {
                    return "bad_sync_bytes b0=" + buf[0].ToString("x2")
                        + " b188=" + buf[188].ToString("x2")
                        + " b376=" + buf[376].ToString("x2");
                }
            }

            int pesStarts = CountPesStarts(path);
            if (pesStarts == 0)
                return "no_pes_payload bytes=" + fileLength;

            return null;
        }
        catch (Exception ex)
        {
            return "read_error " + ex.GetType().Name;
        }
    }

    // Count TS packets whose payload begins with the 0x000001 PES start prefix.
    // See WhyKnotDev's MpegTsValidator.CountPesStarts for the canonical
    // description -- this is a duplicate kept in sync to avoid an upload
    // roundtrip when the encoder produces only system tables.
    internal static int CountPesStarts(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 188 * 64);
            Span<byte> buf = stackalloc byte[188];
            int count = 0;
            while (true)
            {
                int read = stream.Read(buf);
                if (read < 188) break;
                if (buf[0] != 0x47) continue;
                bool pusi = (buf[1] & 0x40) != 0;
                if (!pusi) continue;
                int afc = (buf[3] >> 4) & 0x3;
                int payloadStart;
                if (afc == 1)
                {
                    payloadStart = 4;
                }
                else if (afc == 3)
                {
                    int afLen = buf[4];
                    payloadStart = 5 + afLen;
                }
                else
                {
                    continue;
                }
                if (payloadStart + 3 > 188) continue;
                if (buf[payloadStart] == 0x00 && buf[payloadStart + 1] == 0x00 && buf[payloadStart + 2] == 0x01)
                    count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static string ExtractUrlHost(string url)
    {
        if (string.IsNullOrEmpty(url)) return "?";
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u)) return u.Host;
        }
        catch { /* best-effort */ }
        return "?";
    }
}
