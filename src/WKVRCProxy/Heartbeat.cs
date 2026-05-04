using System.Globalization;
using System.Runtime.Versioning;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Periodic "still alive" line in the watchdog console. Fires every 30
// minutes IF nothing else has logged in the last 5 minutes — silence is
// preferred over noise on a busy console. Format:
//
//   [heartbeat] up=2h13m mesh=connected resolves=47 (3 via lh-yt) stream-bytes=1.2 GB reconnects=0
//
// Useful when the user alt-tabs back to the watchdog console after a long
// session and wants confidence that it's still running and connected. The
// stats also help future bug reports — "resolves=0 reconnects=12" tells a
// very different story than "resolves=200 reconnects=0".
[SupportedOSPlatform("windows")]
internal sealed class Heartbeat : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(5);

    private readonly MeshClient _mesh;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runner;

    public Heartbeat(MeshClient mesh)
    {
        _mesh = mesh;
    }

    public void Start()
    {
        _runner = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_runner != null)
        {
            try { await _runner.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { Tick(); }
            catch (Exception ex)
            {
                Console.WriteLine("[heartbeat][warn] tick threw: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private void Tick()
    {
        // Suppress when something else has logged recently. The user
        // doesn't need a "still alive" reminder when the resolve summary
        // lines are scrolling past every few seconds. Logger.LastWriteUtc
        // is updated on every Tee call (Console.WriteLine + Console.Error
        // + WriteFileOnly).
        if (DateTime.UtcNow - Logger.LastWriteUtc < QuietWindow)
            return;

        TimeSpan up = DateTime.UtcNow - WatchdogStats.StartUtc;
        long resolves = WatchdogStats.ResolvesTotal;
        long lhYt = WatchdogStats.ResolvesViaLhYt;
        long bytes = WatchdogStats.BytesEstimateTotal;
        long reconnects = WatchdogStats.ReconnectCount;

        string meshState = _mesh.IsConnected ? "connected" : "disconnected";

        var sb = new System.Text.StringBuilder();
        sb.Append("[heartbeat] up=").Append(FormatUptime(up));
        sb.Append(" mesh=").Append(meshState);
        sb.Append(" resolves=").Append(resolves);
        if (lhYt > 0) sb.Append(" (").Append(lhYt).Append(" via lh-yt)");
        if (bytes > 0) sb.Append(" stream-bytes=").Append(FormatBytes(bytes));
        sb.Append(" reconnects=").Append(reconnects);

        Console.WriteLine(sb.ToString());
    }

    internal static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
            return ((int)ts.TotalDays).ToString(CultureInfo.InvariantCulture)
                + "d" + ts.Hours.ToString(CultureInfo.InvariantCulture) + "h";
        if (ts.TotalHours >= 1)
            return ((int)ts.TotalHours).ToString(CultureInfo.InvariantCulture)
                + "h" + ts.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
        return ((int)ts.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
    }

    internal static string FormatBytes(long bytes)
    {
        const double KB = 1024;
        const double MB = KB * 1024;
        const double GB = MB * 1024;
        const double TB = GB * 1024;
        if (bytes < KB) return bytes + " B";
        if (bytes < MB) return (bytes / KB).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        if (bytes < GB) return (bytes / MB).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (bytes < TB) return (bytes / GB).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        return (bytes / TB).ToString("0.00", CultureInfo.InvariantCulture) + " TB";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
