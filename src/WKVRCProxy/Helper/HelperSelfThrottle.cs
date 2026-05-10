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

        int gpuLimit = settings.Helper.GpuLimitPercent;
        if (signals.GpuBusyPercent >= Math.Min(95, 100 - Math.Max(5, gpuLimit / 2)))
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
