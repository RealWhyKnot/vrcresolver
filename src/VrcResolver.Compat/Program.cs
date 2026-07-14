using System.Diagnostics;

namespace VrcResolver.Compat;

// Forwarder shipped under the pre-rename exe names during the rename
// transition. The old updater resolves a release payload by the presence of
// WKVRCProxy.exe and relaunches that exact name after the swap; anything
// else on the user's machine (shortcuts, scripts) may also still point at
// the old names. This exe launches the renamed binary sitting next to it,
// forwarding all arguments, and exits.
//
// Dispatch is keyed off our own file name so one binary covers both roles:
//   WKVRCProxy.exe             -> vrcresolver.exe
//   WKVRCProxy.Updater[.*].exe -> vrcresolver.Updater.exe
internal static class Program
{
    private static int Main(string[] args)
    {
        string ownPath = Environment.ProcessPath ?? "";
        string ownName = Path.GetFileNameWithoutExtension(ownPath);
        bool updaterRole = ownName.Contains("Updater", StringComparison.OrdinalIgnoreCase);
        string targetName = updaterRole ? "vrcresolver.Updater.exe" : "vrcresolver.exe";
        string target = Path.Combine(AppContext.BaseDirectory, targetName);

        // Never launch ourselves (a mis-staged copy under the target name
        // would otherwise loop forever).
        if (!File.Exists(target)
            || string.Equals(Path.GetFullPath(target), Path.GetFullPath(ownPath), StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(targetName + " was not found next to " + Path.GetFileName(ownPath)
                + ". Reinstall vrcresolver from the latest release.");
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = target,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        try
        {
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not start " + targetName + ": " + ex.Message);
            return 1;
        }
    }
}
