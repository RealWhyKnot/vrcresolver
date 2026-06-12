using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal static class UpdaterRepair
{
    private const string UpdaterExeName = "WKVRCProxy.Updater.exe";
    private const string StagedUpdaterExeName = "WKVRCProxy.Updater.next.exe";

    public static bool ApplyIfPresent(string installDir)
    {
        string staged = Path.Combine(installDir, StagedUpdaterExeName);
        if (!File.Exists(staged)) return false;

        string target = Path.Combine(installDir, UpdaterExeName);
        string backup = target + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        try
        {
            if (File.Exists(target))
            {
                MoveWithRetry(target, backup, retries: 3);
            }

            MoveWithRetry(staged, target, retries: 3);
            try { if (File.Exists(backup)) File.Delete(backup); } catch { /* best-effort */ }
            ConsoleUx.Success(LogComponent.Update, "updater refreshed for future updates.");
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (!File.Exists(target) && File.Exists(backup))
                    File.Move(backup, target);
            }
            catch { /* best-effort */ }

            Logger.WriteFileOnly("[update] staged updater repair failed: " + ex.GetType().Name + ": " + ex.Message);
            ConsoleUx.Warn(LogComponent.Update, "could not refresh updater yet; it will retry on next launch.");
            return false;
        }
    }

    private static void MoveWithRetry(string src, string dst, int retries)
    {
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                if (File.Exists(dst)) File.Move(src, dst, overwrite: true);
                else File.Move(src, dst);
                return;
            }
            catch (IOException) when (attempt < retries)
            {
                Thread.Sleep(200);
            }
        }
    }
}
