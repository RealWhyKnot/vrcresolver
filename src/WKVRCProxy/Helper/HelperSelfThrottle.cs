namespace WKVRCProxy;

internal readonly record struct HelperRuntimeSignals(
    bool OnBattery,
    bool VrChatRunning,
    int GpuBusyPercent,
    int CpuBusyPercent,
    int ThermalHeadroomPercent,
    int UploadQueueBytes,
    int ConsecutiveFailures);

internal readonly record struct HelperThrottleDecision(
    bool CanAcceptWork,
    string State,
    string Reason);

internal static class HelperSelfThrottle
{
    // Hardcoded back-off threshold. Helper yields when the overall GPU is
    // this percent busy or higher. The previous user-tunable GpuLimitPercent
    // (5..75) was confusingly named and almost universally misread as "max %
    // the helper itself uses." It isn't: helper encode runs on the dedicated
    // NVENC / VCN block independent of the render pipeline, so the cost to
    // the game is near zero in practice. A high threshold (95%) gives the
    // helper near-free reign and still yields when the system is genuinely
    // saturated.
    private const int GpuPauseThresholdPercent = 95;

    public static HelperThrottleDecision Evaluate(AppSettings settings, HelperRuntimeSignals signals)
    {
        if (settings == null)
            return Pause("paused", "settings unavailable");

        settings.Normalize();
        if (!settings.Helper.GpuSharing)
            return Pause("off", "sharing disabled");

        if (signals.OnBattery && !settings.Helper.AllowOnBattery)
            return Pause("paused", "on battery");

        if (signals.ConsecutiveFailures >= 3)
            return Pause("cooldown", "worker errors");

        if (signals.GpuBusyPercent >= GpuPauseThresholdPercent)
            return Pause("paused", "GPU busy");

        if (signals.CpuBusyPercent >= 90)
            return Pause("paused", "CPU busy");

        if (signals.ThermalHeadroomPercent > 0 && signals.ThermalHeadroomPercent < 15)
            return Pause("paused", "thermal headroom low");

        if (settings.Helper.UploadLimitMbps > 0)
        {
            long queueLimitBytes = settings.Helper.UploadLimitMbps * 1024L * 1024L * 4L;
            if (signals.UploadQueueBytes > queueLimitBytes)
                return Pause("paused", "upload queue backed up");
        }

        return new HelperThrottleDecision(true, "idle", signals.VrChatRunning ? "ready without game impact" : "ready");
    }

    private static HelperThrottleDecision Pause(string state, string reason)
    {
        return new HelperThrottleDecision(false, state, reason);
    }
}
