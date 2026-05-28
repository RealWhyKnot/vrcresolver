using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using MessagePack;
using MessagePack.Resolvers;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal sealed partial class MeshClient : IAsyncDisposable
{
    // IHelperLeaseChannel surface, implemented inline so the worker doesn't
        // need to know about MeshClient's internals. The channel exposes the
        // narrow contract: send the helper_window_ready frame, await resolution.
        private bool WindowPullEnabled
            => ServerSupportsFeature(WireConstants.FeatureHelperWindowPull);
    private void QueueHelperStatusRefresh(bool force = false)
        {
            if (!ServerSupportsFeature(WireConstants.FeatureHelperTranscode))
                return;

            DateTime now = DateTime.UtcNow;
            long lastTicks = Interlocked.Read(ref _lastHelperStatusRefreshTicks);
            if (!force && lastTicks > 0 && now - new DateTime(lastTicks, DateTimeKind.Utc) < HelperStatusRefreshInterval)
                return;
            if (Interlocked.CompareExchange(ref _helperStatusRefreshRunning, 1, 0) != 0)
                return;
            Interlocked.Exchange(ref _lastHelperStatusRefreshTicks, now.Ticks);

            _ = Task.Run(async () =>
            {
                try
                {
                    AppSettings settings = AppSettingsStore.Shared.Snapshot();
                    FfmpegCapabilityProbeResult probe = await FfmpegCapabilityProbe.ProbeAsync(
                        AppContext.BaseDirectory,
                        FfmpegCapabilityProbe.DefaultTimeout,
                        CancellationToken.None).ConfigureAwait(false);
                    HelperEncodingQuality quality = await HelperBenchmarkService.ResolveQualityAsync(
                        settings,
                        probe,
                        CancellationToken.None).ConfigureAwait(false);

                    var frame = new HelperStatusFrame
                    {
                        ClientId = _clientId,
                        Sharing = settings.Helper.GpuSharing,
                        CanEncodeH264 = settings.Helper.GpuSharing && probe.CanUseHardwareH264,
                        Status = HelperStatusWord(settings, probe),
                        FfmpegVersion = probe.Version?.Version,
                        Encoder = probe.PreferredEncoder?.EncoderName,
                        EncoderBackend = probe.PreferredEncoder?.Backend.ToString().ToLowerInvariant(),
                        // Field retained on the wire for backward compat with
                        // older server builds that still log it. 0 means "no
                        // user override" -- helper now uses a hardcoded
                        // back-off threshold instead of a configurable knob.
                        GpuLimitPercent = 0,
                        UploadLimitMbps = settings.Helper.UploadLimitMbps,
                        AllowOnBattery = settings.Helper.AllowOnBattery,
                        SmokeTestPassed = probe.SmokeTestPassed ? true : (bool?)false,
                        SmokeTestEncoder = probe.SmokeTestEncoder,
                        // Window-pull opt-in. Always true on this build -- the
                        // worker auto-detects whether the server actually
                        // exercises the path via the welcome feature flag.
                        // Sending it unconditionally keeps the helper_status
                        // shape stable across reconnects.
                        SupportsWindowPull = true,
                        // Visible at advertisement time. Server doesn't need
                        // a real-time stream of this; the 45s refresh cadence
                        // is plenty since the field's only purpose is the
                        // soft-pressure inflight_busy signal.
                        LeaseQueueDepth = Volatile.Read(ref _leaseQueueDepth),
                    };

                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, MeshJsonContext.Default.HelperStatusFrame);
                    await SendTextFrameAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                    bool unchanged = frame.Status == _lastSentHelperStatus
                        && frame.Encoder == _lastSentHelperEncoder;
                    _lastSentHelperStatus = frame.Status;
                    _lastSentHelperEncoder = frame.Encoder;
                    string advertisedSuffix = unchanged ? " (advertised)" : "";
                    string smokeInfo = " smoke=" + (frame.SmokeTestPassed == true ? "passed" : "failed")
                        + (frame.SmokeTestEncoder != null ? " smoke_encoder=" + frame.SmokeTestEncoder : "");
                    string statusLine = "[mesh][helper] status sent status=" + frame.Status
                        + " encoder=" + (frame.Encoder ?? "<none>")
                        + " can_encode_h264=" + frame.CanEncodeH264
                        + " sharing=" + frame.Sharing
                        + " quality=" + HelperEncodingQualityNames.Format(quality)
                        + smokeInfo
                        + advertisedSuffix;
                    Logger.WriteDiagnostic(
                        LogComponent.Helper,
                        statusLine,
                        "status sent status=" + frame.Status
                            + " encoder=" + (frame.Encoder ?? "<none>")
                            + " can_encode_h264=" + frame.CanEncodeH264
                            + " sharing=" + frame.Sharing
                            + " quality=" + HelperEncodingQualityNames.Format(quality)
                            + smokeInfo
                            + advertisedSuffix);
                }
                catch (Exception ex)
                {
                    Logger.WriteFileOnly("[mesh][helper] status send failed: "
                        + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160));
                }
                finally
                {
                    Interlocked.Exchange(ref _helperStatusRefreshRunning, 0);
                }
            });
        }

    private bool ServerSupportsFeature(string feature)
        {
            var features = _serverFeatures;
            return features != null && Array.IndexOf(features, feature) >= 0;
        }

    private async Task SendWindowReadyAsync(HelperWindowReadyFrame frame, CancellationToken ct)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                frame, MeshJsonContext.Default.HelperWindowReadyFrame);
            await SendTextFrameAsync(bytes, ct).ConfigureAwait(false);
        }

    private async Task<HelperWindowResolution> WaitForWindowResolutionAsync(
            string leaseId, TimeSpan ttl, CancellationToken ct)
        {
            // Register before send is the caller's responsibility (worker
            // registers, sends, awaits). Registration is idempotent --
            // duplicate lease_id from a buggy server replaces the prior TCS
            // (the prior await is left dangling and times out on its TTL).
            var tcs = new TaskCompletionSource<HelperWindowResolution>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _windowHolds[leaseId] = tcs;
            try
            {
                using var ttlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ttlCts.CancelAfter(ttl);
                try
                {
                    return await tcs.Task.WaitAsync(ttlCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new HelperWindowResolution(
                        HelperWindowOutcome.TtlExpired,
                        UploadUrlOverride: null,
                        DropReason: WireConstants.HelperDropReasonClientTtlExpired);
                }
            }
            finally
            {
                _windowHolds.TryRemove(leaseId, out _);
            }
        }

    // Adapter so worker code can pass `this` as IHelperLeaseChannel without
        // MeshClient itself implementing the interface publicly (keeps the
        // partial class's public surface unchanged).
        private sealed class HelperLeaseChannelAdapter : IHelperLeaseChannel
        {
            private readonly MeshClient _owner;
            public HelperLeaseChannelAdapter(MeshClient owner) { _owner = owner; }
            public bool WindowPullEnabled => _owner.WindowPullEnabled;
            public Task SendWindowReadyAsync(HelperWindowReadyFrame frame, CancellationToken ct)
                => _owner.SendWindowReadyAsync(frame, ct);
            public Task<HelperWindowResolution> WaitForWindowResolutionAsync(
                string leaseId, TimeSpan ttl, CancellationToken ct)
                => _owner.WaitForWindowResolutionAsync(leaseId, ttl, ct);
        }

    private static string HelperStatusWord(AppSettings settings, FfmpegCapabilityProbeResult probe)
        {
            if (!settings.Helper.GpuSharing) return "off";
            return probe.Status switch
            {
                FfmpegCapabilityProbeStatus.Ready => "idle",
                FfmpegCapabilityProbeStatus.NotFound => "missing_ffmpeg",
                FfmpegCapabilityProbeStatus.NoHardwareEncoder => "no_encoder",
                FfmpegCapabilityProbeStatus.TimedOut => "probe_timeout",
                FfmpegCapabilityProbeStatus.Failed => "probe_failed",
                _ => "paused",
            };
        }

    private void QueueHelperLease(HelperTranscodeLeaseFrame lease)
        {
            if (lease == null || string.IsNullOrWhiteSpace(lease.LeaseId))
                return;

            Logger.WriteDiagnostic(
                LogComponent.Helper,
                "[mesh][helper] lease queued lease=" + LogUtil.SanitizeForConsole(lease.LeaseId, 64)
                    + " stream=" + LogUtil.SanitizeForConsole(lease.PlaybackId, 64)
                    + " segment=" + lease.SegmentIndex
                    + " deadline_ms=" + lease.DeadlineMs
                    + " input=" + LogUtil.RedactUrl(lease.InputUrl),
                "lease queued segment=" + lease.SegmentIndex
                    + " deadline_ms=" + lease.DeadlineMs
                    + " input=" + LogUtil.RedactUrl(lease.InputUrl));

            _ = Task.Run(async () =>
            {
                HelperLeaseRunResult result;
                bool slotAcquired = false;
                try
                {
                    // Bound concurrent leases at the client. Server's
                    // InFlightLimit governs how many leases will be issued
                    // simultaneously to one helper; this mirror keeps us
                    // honest if a server bug ever issues over the cap.
                    // QueueAsync would be cleaner but we want a hard reject
                    // (not a queue-back-pressure) since the server has
                    // already moved on by the time we'd dequeue.
                    if (!s_leaseSlots.Wait(0))
                    {
                        result = new HelperLeaseRunResult(
                            false,
                            "client_inflight_busy",
                            "client lease slots exhausted",
                            0, 0, null, null);
                    }
                    else
                    {
                        slotAcquired = true;
                        Interlocked.Increment(ref _leaseQueueDepth);
                        try
                        {
                            result = await HelperLeaseWorker.RunAsync(
                                lease,
                                AppSettingsStore.Shared.Snapshot(),
                                AppContext.BaseDirectory,
                                _httpClient,
                                new HelperLeaseChannelAdapter(this),
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            result = new HelperLeaseRunResult(
                                false,
                                "error",
                                ex.GetType().Name + ": " + ex.Message,
                                0, 0, null, null);
                        }
                    }
                }
                finally
                {
                    if (slotAcquired)
                    {
                        Interlocked.Decrement(ref _leaseQueueDepth);
                        s_leaseSlots.Release();
                    }
                }

                var frame = new HelperTranscodeResultFrame
                {
                    LeaseId = lease.LeaseId,
                    Success = result.Success,
                    Status = result.Status,
                    Error = result.Error,
                    Bytes = result.Bytes,
                    ElapsedMs = result.ElapsedMilliseconds,
                    Encoder = result.Encoder,
                    FfmpegVersion = result.FfmpegVersion,
                    Phase = result.Phase,
                    HeldMs = result.HeldMs,
                };

                string resultLine = "[mesh][helper] result sent lease=" + LogUtil.SanitizeForConsole(lease.LeaseId, 64)
                    + " segment=" + lease.SegmentIndex
                    + " success=" + result.Success
                    + " status=" + LogUtil.SanitizeForConsole(result.Status, 64)
                    + " bytes=" + result.Bytes
                    + " elapsed_ms=" + result.ElapsedMilliseconds
                    + " encoder=" + (result.Encoder ?? "<none>")
                    + (string.IsNullOrWhiteSpace(result.Error)
                        ? ""
                        : " error=" + LogUtil.SanitizeForConsole(result.Error, 180));

                try
                {
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, MeshJsonContext.Default.HelperTranscodeResultFrame);
                    await SendTextFrameAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                    if (result.Success)
                    {
                        Logger.WriteDiagnostic(LogComponent.Helper, resultLine,
                            "result sent segment=" + lease.SegmentIndex
                                + " status=" + result.Status
                                + " bytes=" + result.Bytes
                                + " elapsed_ms=" + result.ElapsedMilliseconds);
                    }
                    else
                    {
                        Logger.WarnDiagnostic(LogComponent.Helper, resultLine,
                            "result sent segment=" + lease.SegmentIndex
                                + " status=" + result.Status
                                + " bytes=" + result.Bytes
                                + " elapsed_ms=" + result.ElapsedMilliseconds
                                + (string.IsNullOrWhiteSpace(result.Error)
                                    ? ""
                                    : " error=" + LogUtil.SanitizeForConsole(result.Error, 120)));
                    }
                }
                catch (Exception ex)
                {
                    Logger.WarnDiagnostic(
                        LogComponent.Helper,
                        "[mesh][helper][warn] result send failed lease=" + LogUtil.SanitizeForConsole(lease.LeaseId, 64)
                            + " segment=" + lease.SegmentIndex
                            + " " + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 160),
                        "result send failed segment=" + lease.SegmentIndex
                            + " " + ex.GetType().Name + ": " + LogUtil.SanitizeForConsole(ex.Message, 120));
                }
            });
        }

}
