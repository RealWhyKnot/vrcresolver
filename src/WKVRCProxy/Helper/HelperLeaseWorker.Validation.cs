using System.Diagnostics;
using System.Net.Http.Headers;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

internal static partial class HelperLeaseWorker
{
    // Check sync byte 0x47 at offsets 0, 188, 376 -- mirrors the server-side
    // Pre-upload sanity check on the locally-produced TS segment. Returns null on
    // success or a short error string on failure. Mirrors WhyKnotDev's
    // MpegTsValidator gates so bad output is caught locally before an upload
    // roundtrip and a server-side 422.
    //
    // Layers:
    //   1. file size minimum (need at least 3 packets for the sync probe)
    //   2. 0x47 sync byte at offsets 0, 188, 376
    //   3. PES start codes -- at least one PUSI packet whose payload begins
    //      with 0x000001. A file that passes layers 1-2 but emits only PAT/PMT
    //      sections is the cold-encoder failure mode where NVENC writes the
    //      container header before any frame has been decoded.
    internal static string? ValidateMpegTs(string path, long fileLength)
    {
        if (fileLength < 564)
            return "file_too_small bytes=" + fileLength;

        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 564))
            {
                Span<byte> buf = stackalloc byte[564];
                int read = fs.Read(buf);
                if (read < 564)
                    return "short_read read=" + read;

                if (buf[0] != 0x47 || buf[188] != 0x47 || buf[376] != 0x47)
                {
                    return "bad_sync_bytes b0=" + buf[0].ToString("x2")
                        + " b188=" + buf[188].ToString("x2")
                        + " b376=" + buf[376].ToString("x2");
                }
            }

            int pesStarts = CountPesStarts(path);
            if (pesStarts == 0)
                return "no_pes_payload bytes=" + fileLength;

            return null;
        }
        catch (Exception ex)
        {
            return "read_error " + ex.GetType().Name;
        }
    }

    // Count TS packets whose payload begins with the 0x000001 PES start prefix.
    // See WhyKnotDev's MpegTsValidator.CountPesStarts for the canonical
    // description -- this is a duplicate kept in sync to avoid an upload
    // roundtrip when the encoder produces only system tables.
    internal static int CountPesStarts(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 188 * 64);
            Span<byte> buf = stackalloc byte[188];
            int count = 0;
            while (true)
            {
                int read = stream.Read(buf);
                if (read < 188) break;
                if (buf[0] != 0x47) continue;
                bool pusi = (buf[1] & 0x40) != 0;
                if (!pusi) continue;
                int afc = (buf[3] >> 4) & 0x3;
                int payloadStart;
                if (afc == 1)
                {
                    payloadStart = 4;
                }
                else if (afc == 3)
                {
                    int afLen = buf[4];
                    payloadStart = 5 + afLen;
                }
                else
                {
                    continue;
                }
                if (payloadStart + 3 > 188) continue;
                if (buf[payloadStart] == 0x00 && buf[payloadStart + 1] == 0x00 && buf[payloadStart + 2] == 0x01)
                    count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }
}
