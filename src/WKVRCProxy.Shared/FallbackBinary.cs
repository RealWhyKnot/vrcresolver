namespace WKVRCProxy.Shared;

// Picks the native yt-dlp binary the wrapper should hand off to when the mesh is
// unreachable (or otherwise can't resolve), so a failure degrades to VRChat's own
// yt-dlp instead of a blank video. Pure + injectable (exists / isOurWrapper) so the
// ordering is unit-testable without real binaries on disk.
public static class FallbackBinary
{
    // Candidates in priority order:
    //   1. yt-dlp-og.exe next to the wrapper -- the normal de-bundled backup,
    //      present when the wrapper runs from VRChat's Tools dir.
    //   2. yt-dlp-og.exe in VRChat's Tools dir -- PatchManager always writes the
    //      backup there, reached when the wrapper runs from a different dir (e.g.
    //      a dev/dist build invoked directly).
    //   3. VRChat's own yt-dlp.exe in its Tools dir, IF vanilla -- covers the case
    //      where the de-bundle never produced an og backup but VRChat still has its
    //      native binary in place.
    // `isOurWrapper` gates every candidate so we never hand off to one of our own
    // wrappers (which would recurse straight back into this fallback path).
    public static string? Select(
        string exeDir,
        string? vrcToolsDir,
        Func<string, bool> exists,
        Func<string, bool> isOurWrapper)
    {
        string c1 = Path.Combine(exeDir, "yt-dlp-og.exe");
        if (exists(c1) && !isOurWrapper(c1)) return c1;

        if (!string.IsNullOrEmpty(vrcToolsDir))
        {
            string c2 = Path.Combine(vrcToolsDir, "yt-dlp-og.exe");
            if (exists(c2) && !isOurWrapper(c2)) return c2;

            string c3 = Path.Combine(vrcToolsDir, "yt-dlp.exe");
            if (exists(c3) && !isOurWrapper(c3)) return c3;
        }

        return null;
    }
}
