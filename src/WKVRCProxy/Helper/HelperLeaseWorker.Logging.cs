using System.Diagnostics;
using System.Net.Http.Headers;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal static partial class HelperLeaseWorker
{
    private static string CompletedTaskText(Task<string> task, int max)
        {
            if (task.IsCompletedSuccessfully)
                return Snip(task.Result, max);
            return "";
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
