using System.Security.Cryptography;

namespace WKVRCProxy;

internal static class HashUtils
{
    public static string GetFileHash(string filePath)
    {
        if (!File.Exists(filePath)) return string.Empty;
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }
}
