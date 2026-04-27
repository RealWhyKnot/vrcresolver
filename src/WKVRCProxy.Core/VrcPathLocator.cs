using System;
using System.IO;
using System.Runtime.Versioning;

namespace WKVRCProxy.Core;

// Resolves the VRChat Tools directory. Used by PatcherService at runtime and by
// the standalone uninstall.exe — neither has a guaranteed module context, so the
// settings and logger arguments are optional.
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
