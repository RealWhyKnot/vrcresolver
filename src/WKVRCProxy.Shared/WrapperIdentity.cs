using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace WKVRCProxy.Shared;

public enum WrapperKind
{
    // The file is one of ours (current build, prior release, or dev build).
    // Callers must NOT exec it on the fallback path (would recurse) and must
    // NOT preserve it as the og backup.
    Ours,
    // VRChat-bundled yt-dlp -- VRChat ships a modified yt-dlp, not vanilla
    // upstream. Identified by elimination (large PyInstaller-style binary
    // with none of our positive signals). Safe to preserve as yt-dlp-og.exe
    // and to exec on the fallback path.
    VrcBundledYtDlp,
    // Signals were inconclusive. Caller should leave the file alone.
    Unknown,
}

// Multi-signal "is this binary ours?" classifier. Used by PatchManager to
// avoid swapping a dev build out from under itself, and by the wrapper's
// fallback exec path to refuse a recursive exec into another copy of
// ourselves.
//
// Four signals, short-circuited in order:
//   1. Embedded UTF-8 marker baked into every WKVRCProxy build via the
//      MarkerUtf8 literal in this file (NativeAOT emits the bytes
//      directly into rodata so a byte scan finds them).
//   2. PE FileVersionInfo (CompanyName + ProductName).
//   3. SHA-256 against an optional known-release-hashes list shipped with
//      the release artifact. Skipped when the file is missing (dev builds
//      and source-only installs).
//   4. Size band: our wrapper AOT-publishes to ~3-5 MB; the VRChat-bundled
//      yt-dlp (a modified yt-dlp distribution, not vanilla upstream) is
//      PyInstaller-packed at ~17-30 MB. Above the ceiling with all prior
//      signals negative classifies VrcBundledYtDlp; below the ceiling
//      stays Unknown.
[SupportedOSPlatform("windows")]
public static class WrapperIdentity
{
    // Length-mirrored UTF-8 literal and string literal. The UTF-8 form is
    // what byte-scans look for; the string form is exposed for diagnostics
    // and so callers can document the constant. Both must literally appear
    // in the source so the AOT compiler emits them into the binary.
    private const string MarkerString =
        "WKVRCPROXY_WRAPPER_MARKER_v1:9b3e7c8a-7f23-4e6b-9c1d-a4f8e0d2c5b6";
    private static ReadOnlySpan<byte> MarkerUtf8 =>
        "WKVRCPROXY_WRAPPER_MARKER_v1:9b3e7c8a-7f23-4e6b-9c1d-a4f8e0d2c5b6"u8;

    public const int MaxScanBytes = 16 * 1024 * 1024;
    public const long OursSizeCeiling = 10L * 1024 * 1024;

    private const string OurCompanyName = "RealWhyKnot";
    private const string OurProductName = "WKVRCProxy";

    public static string Marker => MarkerString;

    public static WrapperKind Classify(string path, string? knownHashesPath = null)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return WrapperKind.Unknown;

        long size;
        try { size = new FileInfo(path).Length; }
        catch { return WrapperKind.Unknown; }

        // Signal 1: embedded marker. Only scan up to MaxScanBytes; anything
        // larger is automatically not-ours and would only waste IO. We bail
        // early on size > MaxScanBytes since the marker can't be in there
        // if we never wrote one.
        if (size <= MaxScanBytes && ContainsMarker(path)) return WrapperKind.Ours;

        // Signal 2: PE FileVersionInfo. Cheap and definitive when populated.
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            if (string.Equals(fvi.CompanyName, OurCompanyName, StringComparison.Ordinal)
                && string.Equals(fvi.ProductName, OurProductName, StringComparison.Ordinal))
            {
                return WrapperKind.Ours;
            }
        }
        catch { /* FileVersionInfo can fail on non-PE files; fall through */ }

        // Signal 3: SHA against shipped known-release-hashes list. Only fires
        // when the artifact is present; dev workflow and source-only installs
        // skip this silently.
        if (!string.IsNullOrEmpty(knownHashesPath) && File.Exists(knownHashesPath))
        {
            string? sha = ComputeSha256(path);
            if (!string.IsNullOrEmpty(sha) && KnownHashListContains(knownHashesPath, sha))
                return WrapperKind.Ours;
        }

        // Signal 4: size band. PyInstaller-packed yt-dlp (vanilla or
        // VRChat-modified) is always well above 10 MiB; anything under
        // that with no positive signal is Unknown rather than
        // VrcBundledYtDlp -- we'd rather wait than risk exec'ing an
        // arbitrary small binary the user dropped in Tools.
        return size > OursSizeCeiling ? WrapperKind.VrcBundledYtDlp : WrapperKind.Unknown;
    }

    public static bool ContainsMarker(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            long size = new FileInfo(path).Length;
            if (size <= 0 || size > MaxScanBytes) return false;

            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buf = new byte[(int)size];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < MarkerUtf8.Length) return false;
            return buf.AsSpan(0, read).IndexOf(MarkerUtf8) >= 0;
        }
        catch
        {
            return false;
        }
    }

    public static string? ComputeSha256(string path)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] hash = SHA256.HashData(fs);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    // Reads known_wrapper_hashes.txt looking for a leading 64-hex-char field
    // matching the candidate. Format is `<sha256>  <version>  <iso-utc-date>`
    // per release (two-space separator, mirrors sha256sum). Comments (#) and
    // blank lines are ignored. The file is tiny; reading top-to-bottom on
    // every call is fine.
    public static bool KnownHashListContains(string listPath, string sha256Hex)
    {
        try
        {
            foreach (string raw in File.ReadLines(listPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int sp = line.IndexOf(' ');
                string head = sp < 0 ? line : line.Substring(0, sp);
                if (string.Equals(head, sha256Hex, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* missing or unreadable list -> caller treats as "no match" */ }
        return false;
    }
}
