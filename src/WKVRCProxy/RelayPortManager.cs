using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Picks an ephemeral high port on 127.0.0.1 for the local-relay HTTP
// listener and writes it to %LOCALAPPDATA%Low\WKVRCProxy\relay_port.txt
// so the patched yt-dlp wrapper can read it and emit the trust-gateway
// URL (`http://localhost.youtube.com:{port}/play/<session>/manifest.<ext>?target=<base64>`) to
// VRChat instead of raw WhyKnot playback proxy URLs that AVPro's allowlist rejects.
//
// Port-persistence behavior across watchdog restarts: read the previous
// port from disk, try to claim it; on success, reuse so any AVPro request
// in flight against the previous port survives the restart. On failure
// (port now busy, e.g. another process took it), pick a fresh ephemeral.
//
// AOT-clean: only TcpListener + File I/O. No reflection.
[SupportedOSPlatform("windows")]
internal sealed class RelayPortManager
{
    public int CurrentPort { get; private set; }

    private readonly string _portFile;

    public RelayPortManager()
    {
        _portFile = Path.Combine(WkvrcPaths.StateRoot(), "relay_port.txt");
    }

    public bool Initialize()
    {
        Directory.CreateDirectory(WkvrcPaths.StateRoot());

        int? prev = TryReadPreviousPort();
        if (prev.HasValue && TryClaimSpecificPort(prev.Value))
        {
            CurrentPort = prev.Value;
            WritePortFile(CurrentPort);
            Console.WriteLine("[relay] reusing previous port " + CurrentPort);
            return true;
        }

        if (!TryClaimEphemeralPort(out int fresh))
        {
            Console.WriteLine("[relay][error] could not claim any ephemeral port");
            return false;
        }

        CurrentPort = fresh;
        WritePortFile(CurrentPort);
        Console.WriteLine("[relay] listening port " + CurrentPort
            + (prev.HasValue ? " (previous " + prev.Value + " was busy)" : ""));
        return true;
    }

    public void DeletePortFile()
    {
        try { if (File.Exists(_portFile)) File.Delete(_portFile); }
        catch { /* best-effort cleanup */ }
    }

    private int? TryReadPreviousPort()
    {
        try
        {
            if (!File.Exists(_portFile)) return null;
            string text = File.ReadAllText(_portFile).Trim();
            if (int.TryParse(text, out int p) && p > 1024 && p < 65536)
                return p;
        }
        catch { /* best-effort */ }
        return null;
    }

    private static bool TryClaimSpecificPort(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { return false; }
    }

    private static bool TryClaimEphemeralPort(out int port)
    {
        port = 0;
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port > 0;
        }
        catch { return false; }
    }

    private void WritePortFile(int port)
    {
        try
        {
            string tmp = _portFile + ".tmp";
            File.WriteAllText(tmp, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(tmp, _portFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[relay][warn] could not write port file: " + ex.Message);
        }
    }
}
