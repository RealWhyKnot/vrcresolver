using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace WKVRCProxy;

// Pins localhost.youtube.com → 127.0.0.1 in %WINDIR%\System32\drivers\etc\hosts.
// Load-bearing for VRChat's trusted-URL allowlist in PUBLIC instances — without
// it, the relay wrap path can't be reached from a public world. Friends/private
// worlds work either way.
[SupportedOSPlatform("windows")]
internal static class HostsManager
{
    private const string MarkerHost = "localhost.youtube.com";
    public const string AddArg = "--add-hosts-entry";
    public const string RemoveArg = "--remove-hosts-entry";

    private static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static bool IsBypassActive()
    {
        if (!File.Exists(HostsPath)) return false;
        try
        {
            using var fs = new FileStream(HostsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                string t = line.Trim();
                if (t.StartsWith('#')) continue;
                if (t.Contains("127.0.0.1") && t.Contains(MarkerHost)) return true;
            }
        }
        catch { /* best-effort */ }
        return false;
    }

    public static void EnsureBypassEntryOrPrompt()
    {
        if (IsBypassActive()) return;
        Console.WriteLine("Adding hosts entry for public-instance support — UAC prompt incoming.");
        if (!ReexecElevated(AddArg)) return;
        if (IsBypassActive())
            Console.WriteLine("Hosts entry added.");
        else
            Console.WriteLine("Hosts entry not present after elevation — public-instance support may not work.");
    }

    public static void RemoveBypassEntryIfPresent()
    {
        if (!IsBypassActive()) return;
        ReexecElevated(RemoveArg);
    }

    public static int RunAddInElevatedChild()
    {
        if (IsBypassActive()) return 0;
        try
        {
            File.AppendAllText(HostsPath, Environment.NewLine + "127.0.0.1 " + MarkerHost + " # WKVRCProxy" + Environment.NewLine);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("hosts add failed: " + ex.Message);
            return 1;
        }
    }

    public static int RunRemoveInElevatedChild()
    {
        if (!File.Exists(HostsPath)) return 0;
        try
        {
            var lines = File.ReadAllLines(HostsPath);
            var kept = new List<string>(lines.Length);
            foreach (var l in lines)
            {
                string t = l.Trim();
                if (!t.StartsWith('#') && t.Contains("127.0.0.1") && t.Contains(MarkerHost)) continue;
                kept.Add(l);
            }
            File.WriteAllLines(HostsPath, kept);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("hosts remove failed: " + ex.Message);
            return 1;
        }
    }

    private static bool ReexecElevated(string arg)
    {
        string? exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arg,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            // 60s timeout so a UAC dialog left open (user away from keyboard)
            // doesn't block startup forever (Tier D backlog item).
            proc?.WaitForExit(60000);
            if (proc != null && !proc.HasExited)
            {
                Console.WriteLine("[hosts] elevation child still running after 60s — continuing without hosts entry.");
            }
            return true;
        }
        catch (Win32Exception)
        {
            Console.WriteLine("UAC declined — hosts entry not modified.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("hosts elevation error: " + ex.Message);
            return false;
        }
    }
}
