using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;

namespace WKVRCProxy;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string MutexName = "Global\\WKVRCProxy.Watchdog";

    private static int Main(string[] args)
    {
        // Internal re-exec: hosts add/remove from elevated child. Both branches
        // run before mutex acquisition because they exit before the watchdog starts.
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case HostsManager.AddArg: return HostsManager.RunAddInElevatedChild();
                case HostsManager.RemoveArg: return HostsManager.RunRemoveInElevatedChild();
            }
        }

        using var mutex = new System.Threading.Mutex(false, MutexName, out _);
        bool acquired = false;
        try { acquired = mutex.WaitOne(TimeSpan.Zero); }
        catch (AbandonedMutexException) { acquired = true; }

        if (!acquired)
        {
            Console.WriteLine("WKVRCProxy is already running.");
            return 1;
        }

        try
        {
            return RunWatchdog();
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* ignore */ }
        }
    }

    private static int RunWatchdog()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        Console.WriteLine($"WKVRCProxy {version}");

        string installDir = AppContext.BaseDirectory;

        HostsManager.EnsureBypassEntryOrPrompt();

        using var patcher = new PatchManager(installDir);
        patcher.RecoverFromUncleanShutdown();

        var mesh = new MeshClient();
        var ipc = new LocalIpcServer(mesh);
        ipc.Start();
        _ = mesh.StartAsync();

        if (!patcher.Start())
        {
            ShutdownAsync(ipc, mesh, patcher).GetAwaiter().GetResult();
            return 2;
        }

        Console.WriteLine("Patch applied. Watching for VRChat overwrites — Ctrl+C to quit.");
        Console.WriteLine("To uninstall, run WKVRCProxy.Uninstaller.exe (in the same folder).");

        using var quitSignal = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quitSignal.Set();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => quitSignal.Set();

        quitSignal.Wait();
        Console.WriteLine("Shutting down…");

        ShutdownAsync(ipc, mesh, patcher).GetAwaiter().GetResult();
        return 0;
    }

    // Shutdown order per the brief: stop pipe accepting → fail pending TCS to
    // server_unreachable (handled inside MeshClient.StopAsync) → close WS clean
    // → stop PatchManager loop → restore yt-dlp.exe → write clean-exit flag.
    // Total budget 12s.
    private static async Task ShutdownAsync(LocalIpcServer ipc, MeshClient mesh, PatchManager patcher)
    {
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(12);

        async Task WithTimeout(Task t, int ms)
        {
            using var cts = new CancellationTokenSource(ms);
            var done = await Task.WhenAny(t, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
            if (done != t) Console.WriteLine("[shutdown] step exceeded budget — moving on");
        }

        try
        {
            int remain = (int)Math.Max(0, (budget - sw.Elapsed).TotalMilliseconds);
            await WithTimeout(ipc.StopAsync(), Math.Min(remain, 3000)).ConfigureAwait(false);
        }
        catch (Exception ex) { Console.WriteLine("[shutdown] ipc: " + ex.Message); }

        try
        {
            int remain = (int)Math.Max(0, (budget - sw.Elapsed).TotalMilliseconds);
            await WithTimeout(mesh.StopAsync(), Math.Min(remain, 3000)).ConfigureAwait(false);
        }
        catch (Exception ex) { Console.WriteLine("[shutdown] mesh: " + ex.Message); }

        try
        {
            int remain = (int)Math.Max(0, (budget - sw.Elapsed).TotalMilliseconds);
            await WithTimeout(patcher.StopAsync(), Math.Min(remain, 5000)).ConfigureAwait(false);
        }
        catch (Exception ex) { Console.WriteLine("[shutdown] patcher: " + ex.Message); }
    }
}
