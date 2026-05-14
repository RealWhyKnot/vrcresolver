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
    public void ValidateMpegTs_AcceptsFileWithSyncBytesAtAllThreeOffsets()
    {
        string path = Path.Combine(Path.GetTempPath(), "wkvrc-ts-valid-" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            var data = new byte[600];
            data[0] = 0x47;
            data[188] = 0x47;
            data[376] = 0x47;
            File.WriteAllBytes(path, data);
            string? error = HelperLeaseWorker.ValidateMpegTs(path, 600);
            Assert.Null(error);
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
}
