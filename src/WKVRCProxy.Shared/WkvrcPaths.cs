namespace WKVRCProxy.Shared;

// All persistent state for WKVRCProxy lives under
//   %LOCALAPPDATA%Low\WKVRCProxy\
// — the "Low" suffix matters: it's a Low-integrity-writable dir by Windows
// MIC convention. The yt-dlp wrapper deployed into VRChat's Tools dir
// inherits Low integrity (Tools dir lives under %LOCALAPPDATA%Low\), and a
// Low-integrity process cannot write into Medium-integrity locations like
// %LOCALAPPDATA%\WKVRCProxy\ no matter what the DACL says — the Mandatory
// Integrity Control kernel check fires before the DACL check.
//
// The watchdog itself runs at Medium and writes to LocalLow without trouble
// (Medium can write to Low integrity dirs). Symmetric paths means every
// component — watchdog, updater, uninstaller, wrapper — uses the same
// state root regardless of integrity level.
//
// Pre-fix every state path was rooted at LocalApplicationData (Medium).
// Logger.cs, CrashHandler.cs, PatchManager (clean_exit / halt flags),
// CodecInstaller (codec-state.json), YtDlpUpdater (yt-dlp-update-check.json),
// and the YtDlp wrapper's Log() all wrote there. Wrapper writes silently
// failed; pipe connect from wrapper silently failed. Mesh path bypassed.
public static class WkvrcPaths
{
    // C:\Users\<user>\AppData\Local\... → C:\Users\<user>\AppData\LocalLow\...
    // The replacement is a literal trailing "\Local" so it survives non-default
    // profile roots (D:\Users\Bob\AppData\Local works too).
    public static string StateRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string lowdir;
        if (local.EndsWith(@"\Local", StringComparison.OrdinalIgnoreCase))
            lowdir = local[..^"\\Local".Length] + "\\LocalLow";
        else
            lowdir = local + "Low"; // best-effort if the trailing element isn't literal "Local"
        return Path.Combine(lowdir, "WKVRCProxy");
    }

    public static string LogsDir() => Path.Combine(StateRoot(), "logs");
    public static string CrashesDir() => Path.Combine(StateRoot(), "crashes");

    // One-time migration of state from the legacy Medium-integrity location
    // to the new Low-integrity-friendly location. Called once per process
    // on startup. A marker file in the new dir prevents re-running on
    // subsequent launches. Best-effort — failures are logged and the
    // process continues with the new location empty (no historic state).
    public static void MigrateLegacyState(Action<string>? log = null)
    {
        try
        {
            string newRoot = StateRoot();
            string marker = Path.Combine(newRoot, ".migrated-from-localapp");
            if (File.Exists(marker)) return;

            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WKVRCProxy");
            Directory.CreateDirectory(newRoot);

            if (!Directory.Exists(legacy))
            {
                // Nothing to migrate, but plant the marker so we don't keep
                // probing every launch.
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                return;
            }

            int movedDirs = 0, movedFiles = 0;

            foreach (string sub in Directory.EnumerateDirectories(legacy))
            {
                string subName = Path.GetFileName(sub);

                // Don't migrate the logs/ subdir. File.Move preserves the
                // mandatory integrity label of the moved file — log files
                // created by the pre-fix code carry the Medium label, which
                // would block the Low-integrity wrapper from appending after
                // migration. Logs are append-only diagnostic streams, safe
                // to leave behind; new entries land fresh in the LocalLow
                // logs/ dir and inherit Low integrity from the parent.
                // Discard the legacy logs to avoid disk pile-up.
                if (string.Equals(subName, "logs", StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(sub, recursive: true); } catch { /* best-effort */ }
                    continue;
                }

                string dst = Path.Combine(newRoot, subName);
                if (Directory.Exists(dst))
                {
                    // New dir already populated. Move only files that don't
                    // conflict.
                    foreach (string f in Directory.EnumerateFiles(sub))
                    {
                        string fileName = Path.GetFileName(f);
                        string fDst = Path.Combine(dst, fileName);
                        if (!File.Exists(fDst))
                        {
                            try { File.Move(f, fDst); movedFiles++; } catch { /* best-effort */ }
                        }
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { /* best-effort */ }
                }
                else
                {
                    try { Directory.Move(sub, dst); movedDirs++; } catch { /* best-effort */ }
                }
            }

            foreach (string file in Directory.EnumerateFiles(legacy))
            {
                string fileName = Path.GetFileName(file);
                string dst = Path.Combine(newRoot, fileName);
                if (!File.Exists(dst))
                {
                    try { File.Move(file, dst); movedFiles++; } catch { /* best-effort */ }
                }
            }

            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));

            // Clean up the empty legacy dir.
            try { Directory.Delete(legacy, recursive: true); } catch { /* may have leftover */ }

            if (movedDirs > 0 || movedFiles > 0)
                log?.Invoke($"[migrate] state moved from {legacy} → {newRoot} (dirs={movedDirs}, files={movedFiles})");
        }
        catch (Exception ex)
        {
            log?.Invoke("[migrate] failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
