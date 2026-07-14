using System.Text.RegularExpressions;

namespace VrcResolver.Shared;

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
// is "after the watchdog stops, Tools/ contains only vanilla yt-dlp.exe."
public static partial class ToolsDirSweeper
{
    [GeneratedRegex(@"^yt-dlp(-og)?\.exe\.(new|stale)-", RegexOptions.IgnoreCase)]
    private static partial Regex SidecarPattern();

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
                bool match = SidecarPattern().IsMatch(name);
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
                catch { /* best-effort -- file may still be locked; next sweep retries */ }
            }
        }
        catch { /* enumerate failed (permissions, dir vanished mid-sweep) -- best-effort */ }
    }

    // Legacy files left behind in the product's own install/tools/ dir by
    // earlier versions that shipped a bundled vanilla yt-dlp + 24h auto-
    // updater. New installs and clean upgrades never see these; users who
    // upgrade in place from a pre-de-bundle build do, and the Updater's
    // AtomicCopyOver doesn't delete files-not-in-new-dist (intentional --
    // log files and state would be lost). One-shot scrub at watchdog
    // startup; idempotent.
    private static readonly string[] LegacyInstallToolsFiles =
    {
        "yt-dlp-og-fallback.exe",
        "yt-dlp-og-fallback.version.txt",
    };

    public static void SweepLegacyInstallTools(string? installDir)
    {
        if (string.IsNullOrEmpty(installDir)) return;
        string toolsDir = Path.Combine(installDir, "tools");
        if (!Directory.Exists(toolsDir)) return;
        foreach (string name in LegacyInstallToolsFiles)
        {
            string path = Path.Combine(toolsDir, name);
            if (!File.Exists(path)) continue;
            try { File.Delete(path); }
            catch { /* best-effort -- next startup retries */ }
        }
    }
}
