using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class RelayPortManager : IProxyModule
{
    public string Name => "RelayPortManager";
    public int CurrentPort { get; private set; }
    
    private Logger? _logger;
    private string? _portFile;

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;

        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy");
        if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
        _portFile = Path.Combine(appData, "relay_port.dat");

        // Pin the previous session's port across an unclean shutdown. On clean shutdown the file
        // is deleted (see Shutdown), so its presence here means the last process exit didn't run
        // through the normal path — AVPro may still have requests pending against that port. If we
        // can re-bind it, those requests succeed instead of dying with a connection-refused that
        // looks like a silent stall to the user. Falls through to ephemeral selection if the port
        // is now genuinely unavailable.
        int? previousPort = TryReadPreviousPort();
        if (previousPort.HasValue && TryClaimSpecificPort(previousPort.Value))
        {
            WritePortFile(CurrentPort);
            _logger?.Debug("Reusing previous relay port: " + CurrentPort + " (file present from previous session).");
            _logger?.Success("Relay port exported: " + CurrentPort);
            return Task.CompletedTask;
        }

        int? oldPort = previousPort;
        RefreshPort();
        if (oldPort.HasValue && oldPort.Value != CurrentPort)
        {
            _logger?.Info("Relay port changed from " + oldPort.Value + " to " + CurrentPort
                + " (previous port unavailable after unclean shutdown). AVPro requests against the previous port will fail "
                + "until VRChat re-spawns yt-dlp.exe and gets a fresh URL with the new port.");
        }

        return Task.CompletedTask;
    }

    private int? TryReadPreviousPort()
    {
        if (string.IsNullOrEmpty(_portFile) || !File.Exists(_portFile)) return null;
        try
        {
            string txt = File.ReadAllText(_portFile).Trim();
            if (int.TryParse(txt, out int p) && p > 0 && p < 65536) return p;
        }
        catch (Exception ex) { _logger?.Debug("Failed to read previous relay port: " + ex.Message); }
        return null;
    }

    private bool TryClaimSpecificPort(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            CurrentPort = port;
            listener.Stop();
            return true;
        }
        catch { return false; }
    }

    private void WritePortFile(int port)
    {
        try
        {
            if (string.IsNullOrEmpty(_portFile)) return;
            using var fs = new FileStream(_portFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fs);
            writer.Write(port.ToString());
        }
        catch (Exception ex)
        {
            _logger?.Warning("Failed to write relay port file: " + ex.Message);
        }
    }

    public void RefreshPort()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            CurrentPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            _logger?.Debug($"Assigned ephemeral relay port: {CurrentPort}");

            // _portFile is set up in InitializeAsync; re-derive defensively if RefreshPort is
            // called on a path that bypassed it (shouldn't happen today, but cheap guard).
            if (string.IsNullOrEmpty(_portFile))
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy");
                if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
                _portFile = Path.Combine(appData, "relay_port.dat");
            }

            WritePortFile(CurrentPort);
            _logger?.Success($"Relay port exported: {CurrentPort}");
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to initialize or export relay port: " + ex.Message);
            CurrentPort = 0;
        }
    }

    public ModuleHealthReport GetHealthReport()
    {
        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = CurrentPort > 0 ? HealthStatus.Healthy : HealthStatus.Failed,
            Reason = CurrentPort > 0 ? "" : "Failed to bind relay port",
            LastChecked = DateTime.Now
        };
    }

    public void Shutdown()
    {
        if (!string.IsNullOrEmpty(_portFile) && File.Exists(_portFile))
        {
            try
            {
                File.Delete(_portFile);
            }
            catch (Exception ex)
            {
                _logger?.Warning("Failed to cleanup relay port file: " + ex.Message);
            }
        }
    }
}
