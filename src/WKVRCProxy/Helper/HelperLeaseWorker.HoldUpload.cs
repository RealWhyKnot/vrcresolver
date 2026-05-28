using System.Diagnostics;
using System.Net.Http.Headers;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal static partial class HelperLeaseWorker
{
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
}
