namespace WKVRCProxy;

internal enum FfmpegLocationKind
{
    Bundled,
    Path,
}

internal readonly record struct FfmpegLocation(string Path, FfmpegLocationKind Kind);

internal static class FfmpegLocator
{
    public static FfmpegLocation? Locate(string installDirectory, string? pathEnvironment = null)
    {
        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            string bundled = System.IO.Path.Combine(installDirectory, "tools", "ffmpeg.exe");
            if (File.Exists(bundled))
                return new FfmpegLocation(bundled, FfmpegLocationKind.Bundled);
        }

        pathEnvironment ??= Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnvironment))
            return null;

        foreach (string entry in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = System.IO.Path.Combine(entry, "ffmpeg.exe");
            if (File.Exists(candidate))
                return new FfmpegLocation(candidate, FfmpegLocationKind.Path);
        }

        return null;
    }
}
