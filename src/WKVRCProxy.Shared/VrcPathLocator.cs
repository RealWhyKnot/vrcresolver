using System.Runtime.Versioning;

namespace WKVRCProxy.Shared;

// Locates VRChat's Tools directory (where its bundled yt-dlp.exe lives and where
// PatchManager preserves the vanilla copy as yt-dlp-og.exe). Shared so both the
// watchdog (which de-bundles + patches) and the yt-dlp wrapper (which falls back
// to the vanilla binary when the mesh is unreachable) resolve the same path.
[SupportedOSPlatform("windows")]
public static class VrcPathLocator
{
    public static string? Find(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath) && Directory.Exists(customPath))
            return customPath;

        string localLow = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
            "VRChat", "VRChat", "Tools");
        if (Directory.Exists(localLow)) return localLow;

        return null;
    }
}
