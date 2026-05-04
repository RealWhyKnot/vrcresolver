using System.Text.RegularExpressions;

namespace WKVRCProxy.Shared;

// Sweeps watchdog-authored sidecar files out of VRChat's Tools directory.
// Matches files we know we created:
//   * ".new-<short>" — atomic-copy temps from PatchManager
//   * ".stale-<utc>" — locked-target rename-asides
//   * "yt-dlp-wrapper.log" — legacy wrapper diagnostic file. Pre-fix the
//     wrapper wrote there directly; current code writes to %LOCALAPPDATA%
//     instead, but we still scrub the literal filename so any residue
//     from earlier-version installs gets auto-cleaned on next launch.
// Everything else is untouched — VRChat or the patched yt-dlp may have its
// own files there and we don't second-guess them.
//
// Called at every transition that ought to leave Tools/ pristine: startup
// recovery, clean shutdown, halt, and uninstall. The user-visible invariant
// is "after WKVRCProxy stops, Tools/ contains only vanilla yt-dlp.exe."
public static class ToolsDirSweeper
{
    private static readonly Regex SidecarPattern = new(
        @"^yt-dlp(-og)?\.exe\.(new|stale)-",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] LiteralResidueNames =
    {
        "yt-dlp-wrapper.log",
    };

    public static void Sweep(string? toolsDir)
    {
        if (string.IsNullOrEmpty(toolsDir)) return;
        if (!Directory.Exists(toolsDir)) return;
        try
        {
            foreach (string path in Directory.EnumerateFiles(toolsDir))
            {
                string name = Path.GetFileName(path);
                bool match = SidecarPattern.IsMatch(name);
                if (!match)
                {
                    foreach (var literal in LiteralResidueNames)
                    {
                        if (string.Equals(name, literal, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                            break;
                        }
                    }
                }
                if (!match) continue;
                try { File.Delete(path); }
                catch { /* best-effort — file may still be locked; next sweep retries */ }
            }
        }
        catch { /* enumerate failed (permissions, dir vanished mid-sweep) — best-effort */ }
    }
}
