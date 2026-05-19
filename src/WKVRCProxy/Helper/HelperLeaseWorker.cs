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
    string? FfmpegVersion);

internal static class HelperLeaseWorker
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public static async Task<HelperLeaseRunResult> RunAsync(
        HelperTranscodeLeaseFrame leaseFrame,
        AppSettings settings,
        string installDirectory,
        HttpClient httpClient,
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
    // stream. Best-effort: any failure short-circuits to null so the caller
    // can decide what to do (the normal case is "skip the truncated-video
    // retry and proceed to upload"; the validator on the server will catch
    // the file if it's structurally bad).
    private static async Task<double?> ProbeVideoDurationAsync(
        string ffmpegPath,
        string outputPath,
        CancellationToken ct)
    {
        string? ffprobePath = TryResolveFfprobePath(ffmpegPath);
        if (string.IsNullOrEmpty(ffprobePath)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=duration");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("v:0");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add(outputPath);

        try
        {
            using var p = new Process { StartInfo = psi };
            p.Start();
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0) return null;
            string stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            if (string.IsNullOrEmpty(stdout)) return null;
            if (double.TryParse(stdout, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double secs)
                && secs > 0)
            {
                return secs;
            }
            return null;
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
