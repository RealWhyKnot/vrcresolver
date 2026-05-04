using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Per-install stable client identity. Persisted at
//   %LOCALAPPDATA%Low\WKVRCProxy\client_id.txt
// (next to clean_exit.flag, codec-state.json, v3_welcome_cache.json — same
// LocalLow state-root convention from project_locallow_state_layout.md).
//
// Used as the client_id field on:
//   * v3 ClientHelloFrame (server keys server-side telemetry on this).
//   * playback_feedback frames (correlates a returning watchdog to its
//     prior failure reports without socket-address matching).
//
// Pre-fix this was a fresh Guid.NewGuid() in MeshClient's field
// initializer — fresh per process launch. The watchdog launches once per
// VRChat session, so server-side analytics keying on client_id couldn't
// recognise a returning user across launches. Persisting the GUID once
// and reading it on subsequent launches makes the same install present
// the same identity for its full lifetime.
//
// Identity rotates only on:
//   * Uninstall + reinstall (the file lives under the LocalLow state
//     root which the uninstaller wipes).
//   * Manual deletion of the client_id.txt file.
//   * A fresh install on a different user account (different LocalLow
//     root).
//
// Best-effort throughout: read failures (permissions, corrupt content),
// write failures (disk full, file locked), or directory-create failures
// all degrade to a fresh in-memory GUID for this run rather than
// throwing — server-side analytics tolerate "client we've never seen"
// gracefully.
internal static class ClientIdentity
{
    internal const string FileName = "client_id.txt";

    public static string LoadOrCreate() => LoadOrCreate(DefaultPath());

    // Test-only overload: caller supplies the file path so unit tests can
    // exercise the load/create/corrupt-file paths in a temp dir without
    // touching the real LocalLow state root.
    internal static string LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string content = File.ReadAllText(path).Trim();
                if (Guid.TryParseExact(content, "N", out var g))
                    return g.ToString("N");
                // File present but contents not a 32-hex-digit GUID —
                // treat as corrupt, regenerate. Don't throw.
            }
        }
        catch { /* fall through to generate + write */ }

        string fresh = Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".new";
            File.WriteAllText(tmp, fresh);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // In-memory only this run. Next launch will retry the file
            // and either succeed (if the transient cause cleared) or
            // generate ANOTHER fresh GUID. Server sees two short-lived
            // identities — same as pre-persistence behaviour, no worse.
        }
        return fresh;
    }

    private static string DefaultPath() =>
        Path.Combine(WkvrcPaths.StateRoot(), FileName);
}
