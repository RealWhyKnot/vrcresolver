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
            TranscodeFfmpegCommand command = TranscodeWorkerProcess.BuildSegmentCommand(
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

            if (ffmpegResult.ExitCode != 0)
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
