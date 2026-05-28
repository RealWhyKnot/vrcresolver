using System.Diagnostics;
using System.Net.Http.Headers;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal static partial class HelperLeaseWorker
{
    // Probe the audio stream's duration via ffprobe. Returns null on any
        // failure (no audio stream, ffprobe missing, parse error). Symmetric
        // with ProbeVideoDurationAsync; the server's metrics validator uses the
        // pair to apply the truncated-video rule (video < audio/2).
        private static Task<double?> ProbeAudioDurationAsync(
            string ffmpegPath,
            string outputPath,
            CancellationToken ct)
            => ProbeStreamDurationAsync(ffmpegPath, outputPath, "a:0", ct);

    // Probe the video stream's duration via ffprobe. Returns null when the
        // probe can't be run, the binary is missing, or the file has no video
        // stream. The caller treats null as zero in the announced metrics; the
        // server's validator rejects zero so the lease falls back to server CPU.
        private static Task<double?> ProbeVideoDurationAsync(
            string ffmpegPath,
            string outputPath,
            CancellationToken ct)
            => ProbeStreamDurationAsync(ffmpegPath, outputPath, "v:0", ct);
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

}
