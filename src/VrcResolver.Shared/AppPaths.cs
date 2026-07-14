namespace VrcResolver.Shared;

// All persistent state for vrcresolver lives under
//   %LOCALAPPDATA%Low\vrcresolver\
// -- the "Low" suffix matters: it's a Low-integrity-writable dir by Windows
// MIC convention. The yt-dlp wrapper deployed into VRChat's Tools dir
// inherits Low integrity (Tools dir lives under %LOCALAPPDATA%Low\), and a
// Low-integrity process cannot write into Medium-integrity locations like
// %LOCALAPPDATA%\vrcresolver\ no matter what the DACL says -- the Mandatory
// Integrity Control kernel check fires before the DACL check.
//
// The watchdog itself runs at Medium and writes to LocalLow without trouble
// (Medium can write to Low integrity dirs). Symmetric paths means every
// component -- watchdog, updater, uninstaller, wrapper -- uses the same
// state root regardless of integrity level.
//
// History: state was originally rooted at LocalApplicationData (Medium),
// then moved to LocalLow under the pre-rename product dir name, and now
// lives under the renamed dir. MigrateFromLegacyProduct runs the full
// chain on startup so any older install lands in the current location.
public static class AppPaths
{
    private const string ProductDirName = "vrcresolver";

    // Marker file planted in the renamed state root after the copy from the
    // pre-rename root completes. Its presence short-circuits the migration
    // probe on subsequent launches.
    public const string RenameMigrationMarker = ".migrated-from-wkvrcproxy";

    // C:\Users\<user>\AppData\Local\... -> C:\Users\<user>\AppData\LocalLow\...
    // The replacement is a literal trailing "\Local" so it survives non-default
    // profile roots (D:\Users\Bob\AppData\Local works too).
    private static string LocalLowDir()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.EndsWith(@"\Local", StringComparison.OrdinalIgnoreCase))
            return local[..^"\\Local".Length] + "\\LocalLow";
        return local + "Low"; // best-effort if the trailing element isn't literal "Local"
    }

    public static string StateRoot() => Path.Combine(LocalLowDir(), ProductDirName);

    public static string LogsDir() => Path.Combine(StateRoot(), "logs");
    public static string CrashesDir() => Path.Combine(StateRoot(), "crashes");

    // Machine-wide state (TLS ports file, certificate leftovers) written by
    // the elevated relay bootstrap.
    public static string ProgramDataRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductDirName);

    // One-time migration chain, run once per process on startup BEFORE any
    // logger / crash handler opens files:
    //   1. If the renamed LocalLow root is untouched, finish the oldest
    //      migration first (LocalApplicationData -> LocalLow, both under the
    //      pre-rename dir name) exactly as pre-rename builds did, so the
    //      pre-rename root is complete before it is copied.
    //   2. Copy the pre-rename LocalLow root into the renamed root (skip
    //      logs\), plant RenameMigrationMarker, and leave the old dir in
    //      place -- an un-repatched Low-integrity wrapper may still need to
    //      read staged files there until PatchManager swaps it.
    //   3. Copy %PROGRAMDATA%\<old>\ -> %PROGRAMDATA%\<new>\ (TLS ports
    //      file and any certificate file leftovers), same leave-in-place
    //      rule.
    // Best-effort -- failures are logged and the process continues with an
    // empty renamed root (no historic state).
    public static void MigrateFromLegacyProduct(Action<string>? log = null)
    {
        try
        {
            string newRoot = StateRoot();
            if (!RenamedRootAlreadyPopulated(newRoot))
            {
                string legacyLowRoot = Path.Combine(LocalLowDir(), LegacyCompat.LegacyStateDirName);
                string legacyLocalApp = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    LegacyCompat.LegacyStateDirName);
                if (!Directory.Exists(legacyLowRoot) && Directory.Exists(legacyLocalApp))
                    MigrateLegacyLocalAppState(legacyLocalApp, legacyLowRoot, log);
                if (Directory.Exists(legacyLowRoot))
                    CopyLegacyRoot(legacyLowRoot, newRoot, log);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("[migrate] rename migration failed: " + ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            string legacyProgramData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                LegacyCompat.LegacyStateDirName);
            MigrateProgramData(legacyProgramData, ProgramDataRoot(), log);
        }
        catch (Exception ex)
        {
            log?.Invoke("[migrate] ProgramData migration failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // Idempotence gate: marker present, or the renamed root already has any
    // content (a fresh install that never had legacy state, or a migration
    // that already ran).
    internal static bool RenamedRootAlreadyPopulated(string newRoot)
    {
        if (File.Exists(Path.Combine(newRoot, RenameMigrationMarker))) return true;
        try
        {
            return Directory.Exists(newRoot) && Directory.EnumerateFileSystemEntries(newRoot).Any();
        }
        catch
        {
            return false;
        }
    }

    // COPY (not move) the pre-rename LocalLow root into the renamed root.
    // logs\ is skipped: append-only diagnostic streams whose files also
    // carry old integrity labels; fresh logs land in the new root. The
    // source dir is intentionally left in place -- see the caller comment.
    internal static void CopyLegacyRoot(string legacyRoot, string newRoot, Action<string>? log = null)
    {
        Directory.CreateDirectory(newRoot);
        int copied = 0;

        foreach (string dir in Directory.EnumerateDirectories(legacyRoot, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(legacyRoot, dir);
            if (IsUnderLogs(rel)) continue;
            try { Directory.CreateDirectory(Path.Combine(newRoot, rel)); } catch { /* best-effort */ }
        }

        foreach (string file in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(legacyRoot, file);
            if (IsUnderLogs(rel)) continue;
            string dst = Path.Combine(newRoot, rel);
            if (File.Exists(dst)) continue;
            try { File.Copy(file, dst); copied++; } catch { /* best-effort */ }
        }

        File.WriteAllText(Path.Combine(newRoot, RenameMigrationMarker), DateTime.UtcNow.ToString("o"));
        if (copied > 0)
            log?.Invoke($"[migrate] state copied from {legacyRoot} -> {newRoot} (files={copied})");
    }

    private static bool IsUnderLogs(string relativePath)
    {
        string first = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return string.Equals(first, "logs", StringComparison.OrdinalIgnoreCase);
    }

    // ProgramData copy: TLS ports file + any certificate file leftovers.
    // Skips when the new dir already has content; leaves the old dir for
    // the elevated cleanup path to remove.
    internal static void MigrateProgramData(string legacyRoot, string newRoot, Action<string>? log = null)
    {
        if (!Directory.Exists(legacyRoot)) return;
        try
        {
            if (Directory.Exists(newRoot) && Directory.EnumerateFileSystemEntries(newRoot).Any()) return;
        }
        catch { return; }

        Directory.CreateDirectory(newRoot);
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(legacyRoot))
        {
            string dst = Path.Combine(newRoot, Path.GetFileName(file));
            if (File.Exists(dst)) continue;
            try { File.Copy(file, dst); copied++; } catch { /* best-effort */ }
        }
        if (copied > 0)
            log?.Invoke($"[migrate] machine state copied from {legacyRoot} -> {newRoot} (files={copied})");
    }

    // Oldest migration step: move state from the Medium-integrity
    // LocalApplicationData location into the LocalLow root -- both under the
    // PRE-RENAME dir name, exactly as pre-rename builds did it, so the copy
    // step above sees a complete legacy root. A marker file in the legacy
    // LocalLow dir prevents re-running. Best-effort throughout.
    internal static void MigrateLegacyLocalAppState(string legacySource, string legacyLowRoot, Action<string>? log = null)
    {
        try
        {
            string marker = Path.Combine(legacyLowRoot, ".migrated-from-localapp");
            if (File.Exists(marker)) return;

            Directory.CreateDirectory(legacyLowRoot);

            if (!Directory.Exists(legacySource))
            {
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                return;
            }

            int movedDirs = 0, movedFiles = 0;

            foreach (string sub in Directory.EnumerateDirectories(legacySource))
            {
                string subName = Path.GetFileName(sub);

                // Don't migrate the logs/ subdir. File.Move preserves the
                // mandatory integrity label of the moved file -- log files
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

                string dst = Path.Combine(legacyLowRoot, subName);
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

            foreach (string file in Directory.EnumerateFiles(legacySource))
            {
                string fileName = Path.GetFileName(file);
                string dst = Path.Combine(legacyLowRoot, fileName);
                if (!File.Exists(dst))
                {
                    try { File.Move(file, dst); movedFiles++; } catch { /* best-effort */ }
                }
            }

            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));

            // Clean up the empty legacy dir.
            try { Directory.Delete(legacySource, recursive: true); } catch { /* may have leftover */ }

            if (movedDirs > 0 || movedFiles > 0)
                log?.Invoke($"[migrate] state moved from {legacySource} -> {legacyLowRoot} (dirs={movedDirs}, files={movedFiles})");
        }
        catch (Exception ex)
        {
            log?.Invoke("[migrate] failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
