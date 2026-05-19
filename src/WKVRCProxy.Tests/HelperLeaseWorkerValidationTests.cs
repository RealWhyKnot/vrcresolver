using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

public class HelperLeaseWorkerValidationTests
{
    [Fact]
    public void ValidateMpegTs_RejectsFileSmallerThan564Bytes()
    {
        string path = Path.Combine(Path.GetTempPath(), "wkvrc-ts-small-" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            File.WriteAllBytes(path, new byte[200]);
            string? error = HelperLeaseWorker.ValidateMpegTs(path, 200);
            Assert.NotNull(error);
            Assert.Contains("too_small", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ValidateMpegTs_RejectsFileWithNoSyncBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), "wkvrc-ts-nosync-" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            // 1 KB of 0x00 -- no sync bytes
            File.WriteAllBytes(path, new byte[1024]);
            string? error = HelperLeaseWorker.ValidateMpegTs(path, 1024);
            Assert.NotNull(error);
            Assert.Contains("bad_sync", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ValidateMpegTs_RejectsFileWithSyncByteOnlyAtOffset0()
    {
        string path = Path.Combine(Path.GetTempPath(), "wkvrc-ts-partsync-" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            var data = new byte[600];
            data[0] = 0x47;
            // offsets 188 and 376 remain 0x00
            File.WriteAllBytes(path, data);
            string? error = HelperLeaseWorker.ValidateMpegTs(path, 600);
            Assert.NotNull(error);
            Assert.Contains("bad_sync", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ValidateMpegTs_AcceptsFileWithSyncBytesAndPesPayload()
    {
        string path = Path.Combine(Path.GetTempPath(), "wkvrc-ts-valid-" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            File.WriteAllBytes(path, ValidTsBytesWithPes(4));
            string? error = HelperLeaseWorker.ValidateMpegTs(path, 4 * 188);
            Assert.Null(error);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ValidateMpegTs_RejectsSyncBytesWithoutPesPayload()
    {
        string path = Path.Combine(Path.GetTempPath(), "wkvrc-ts-nopes-" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            // 600 bytes, sync at 0/188/376 but no PES start codes anywhere --
            // this is the cold-encoder failure shape (PAT/PMT only, no
            // elementary stream).
            var data = new byte[600];
            data[0] = 0x47;
            data[188] = 0x47;
            data[376] = 0x47;
            File.WriteAllBytes(path, data);
            string? error = HelperLeaseWorker.ValidateMpegTs(path, 600);
            Assert.NotNull(error);
            Assert.Contains("no_pes_payload", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CountPesStarts_FindsAllPusiPayloadPackets()
    {
        string path = Path.Combine(Path.GetTempPath(), "wkvrc-ts-pescount-" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            File.WriteAllBytes(path, ValidTsBytesWithPes(5));
            Assert.Equal(5, HelperLeaseWorker.CountPesStarts(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CountPesStarts_ReturnsZeroForSyncOnlyFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "wkvrc-ts-synconly-" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            var data = new byte[5 * 188];
            for (int i = 0; i < 5; i++) data[i * 188] = 0x47;
            File.WriteAllBytes(path, data);
            Assert.Equal(0, HelperLeaseWorker.CountPesStarts(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ValidateMpegTs_RejectsZeroLengthFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "wkvrc-ts-zero-" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            File.WriteAllBytes(path, Array.Empty<byte>());
            string? error = HelperLeaseWorker.ValidateMpegTs(path, 0);
            Assert.NotNull(error);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // Build a TS byte sequence where every packet is a PUSI payload-only packet
    // starting with the PES start prefix 0x00 0x00 0x01.
    private static byte[] ValidTsBytesWithPes(int packetCount)
    {
        var buf = new byte[packetCount * 188];
        for (int i = 0; i < packetCount; i++)
        {
            int off = i * 188;
            buf[off + 0] = 0x47; // sync
            buf[off + 1] = 0x40; // PUSI=1
            buf[off + 2] = 0x00;
            buf[off + 3] = 0x10; // AFC=01 payload-only, CC=0
            buf[off + 4] = 0x00; // PES start prefix
            buf[off + 5] = 0x00;
            buf[off + 6] = 0x01;
        }
        return buf;
    }
}
