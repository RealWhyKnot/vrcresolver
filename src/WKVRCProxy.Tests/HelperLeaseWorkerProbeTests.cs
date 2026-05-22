using System.Diagnostics;
using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

// Integration tests for the helper-side ffprobe path. The shipped probe
// silently returned zero for every helper-encoded TS file in 2026-05-22
// playback because the mpegts muxer left per-stream duration tags empty;
// the validator on the server then had to backstop with a re-probe after
// the upload. These tests synthesize the same shape locally so the
// fallback to container-level duration stays wired and a future probe
// regression fails CI instead of producing silent zeros at runtime.
//
// All tests are skipped (via early return) when the bundled ffmpeg toolset
// is not present alongside the test bin dir -- that lets the suite still
// pass on CI runs where dist/tools/ wasn't populated.
public class HelperLeaseWorkerProbeTests
{
    [Fact]
    public async Task ProbeStreamDuration_ReadsStreamLevelDuration_ForVanillaTs()
    {
        if (!BundledFfmpegAvailable(out string ffmpegPath, out _))
            return; // skip: tools not staged

        string outputPath = NewTempTsPath();
        try
        {
            await RunFfmpegAsync(ffmpegPath, BuildPlainEncodeArgs(outputPath, durationSec: 2));

            double? video = await HelperLeaseWorker.ProbeStreamDurationAsync(
                ffmpegPath, outputPath, "v:0", CancellationToken.None);
            Assert.NotNull(video);
            Assert.InRange(video!.Value, 1.5, 2.5);

            double? audio = await HelperLeaseWorker.ProbeStreamDurationAsync(
                ffmpegPath, outputPath, "a:0", CancellationToken.None);
            Assert.NotNull(audio);
            Assert.InRange(audio!.Value, 1.5, 2.5);
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    // Reproduces the 2026-05-22 helper-output shape: -output_ts_offset
    // pushes the initial PTS, -avoid_negative_ts disabled keeps it from
    // being shifted, and the mpegts muxer ends up not tagging per-stream
    // duration. The previous probe shape returned empty stdout for this
    // file; with the format-level fallback the probe now returns the
    // container duration so the wire metric is a real number again.
    [Fact]
    public async Task ProbeStreamDuration_FallsBackToFormatLevel_ForHelperShapedTs()
    {
        if (!BundledFfmpegAvailable(out string ffmpegPath, out _))
            return;

        string outputPath = NewTempTsPath();
        try
        {
            await RunFfmpegAsync(
                ffmpegPath,
                BuildHelperShapedEncodeArgs(outputPath, startOffsetSec: 32.032, durationSec: 4));

            double? video = await HelperLeaseWorker.ProbeStreamDurationAsync(
                ffmpegPath, outputPath, "v:0", CancellationToken.None);
            Assert.NotNull(video);
            Assert.InRange(video!.Value, 3.5, 4.5);

            double? audio = await HelperLeaseWorker.ProbeStreamDurationAsync(
                ffmpegPath, outputPath, "a:0", CancellationToken.None);
            Assert.NotNull(audio);
            Assert.InRange(audio!.Value, 3.5, 4.5);
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    // When the lease wired HasAudio=true but the encoded TS turned out to
    // be video-only (helper bug, broken source, missing audio rendition),
    // the audio probe must still return null instead of mis-reporting the
    // video length as audio. The server validator then rejects with
    // audio_duration_zero and the lease falls back to server CPU.
    [Fact]
    public async Task ProbeStreamDuration_ReturnsNull_ForMissingStreamType()
    {
        if (!BundledFfmpegAvailable(out string ffmpegPath, out _))
            return;

        string outputPath = NewTempTsPath();
        try
        {
            await RunFfmpegAsync(
                ffmpegPath,
                BuildVideoOnlyEncodeArgs(outputPath, durationSec: 2));

            double? video = await HelperLeaseWorker.ProbeStreamDurationAsync(
                ffmpegPath, outputPath, "v:0", CancellationToken.None);
            Assert.NotNull(video);
            Assert.InRange(video!.Value, 1.5, 2.5);

            double? audio = await HelperLeaseWorker.ProbeStreamDurationAsync(
                ffmpegPath, outputPath, "a:0", CancellationToken.None);
            Assert.Null(audio);
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    [Fact]
    public async Task ProbeStreamDuration_ReturnsNull_ForEmptyFile()
    {
        if (!BundledFfmpegAvailable(out string ffmpegPath, out _))
            return;

        string outputPath = NewTempTsPath();
        try
        {
            File.WriteAllBytes(outputPath, Array.Empty<byte>());

            double? video = await HelperLeaseWorker.ProbeStreamDurationAsync(
                ffmpegPath, outputPath, "v:0", CancellationToken.None);
            Assert.Null(video);
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    [Fact]
    public async Task ProbeStreamDuration_ReturnsNull_WhenFfprobeNotAlongsideFfmpeg()
    {
        // Synthesize a directory that has ffmpeg.exe but not ffprobe.exe.
        // TryResolveFfprobePath looks adjacent to the supplied ffmpeg path;
        // when it returns null the probe must surface as null (not throw,
        // not silently fall through to a stale binary on PATH).
        string isolated = Path.Combine(Path.GetTempPath(),
            "wkvrc-noffprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        string fakeFfmpeg = Path.Combine(isolated, "ffmpeg.exe");
        File.WriteAllBytes(fakeFfmpeg, new byte[] { 0x4D, 0x5A });
        try
        {
            double? video = await HelperLeaseWorker.ProbeStreamDurationAsync(
                fakeFfmpeg, "anything.ts", "v:0", CancellationToken.None);
            Assert.Null(video);
        }
        finally
        {
            try { Directory.Delete(isolated, recursive: true); } catch { }
        }
    }

    private static bool BundledFfmpegAvailable(out string ffmpegPath, out string ffprobePath)
    {
        foreach (string root in CandidateRoots())
        {
            string toolsDir = Path.Combine(root, "dist", "tools");
            string mpeg = Path.Combine(toolsDir, "ffmpeg.exe");
            string probe = Path.Combine(toolsDir, "ffprobe.exe");
            if (File.Exists(mpeg) && File.Exists(probe))
            {
                ffmpegPath = mpeg;
                ffprobePath = probe;
                return true;
            }
        }
        ffmpegPath = "";
        ffprobePath = "";
        return false;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? d = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(d))
        {
            if (seen.Add(d)) yield return d;
            d = Directory.GetParent(d)?.FullName;
        }
    }

    private static string NewTempTsPath() =>
        Path.Combine(Path.GetTempPath(), "wkvrc-probe-" + Guid.NewGuid().ToString("N") + ".ts");

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    // testsrc2 + sine, libx264 + aac, plain mpegts -- the muxer tags
    // per-stream duration in this shape.
    private static string[] BuildPlainEncodeArgs(string outputPath, int durationSec) => new[]
    {
        "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
        "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=" + durationSec,
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=" + durationSec,
        "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
        "-pix_fmt", "yuv420p", "-g", "60",
        "-c:a", "aac", "-b:a", "128k", "-ac", "2",
        "-t", durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-f", "mpegts", outputPath,
    };

    // Mirrors the helper's encode shape: -output_ts_offset + -avoid_negative_ts disabled.
    private static string[] BuildHelperShapedEncodeArgs(
        string outputPath, double startOffsetSec, int durationSec) => new[]
    {
        "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
        "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=" + durationSec,
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=" + durationSec,
        "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
        "-pix_fmt", "yuv420p", "-g", "60",
        "-c:a", "aac", "-b:a", "128k", "-ac", "2",
        "-t", durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-output_ts_offset", startOffsetSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        "-avoid_negative_ts", "disabled",
        "-f", "mpegts", outputPath,
    };

    private static string[] BuildVideoOnlyEncodeArgs(string outputPath, int durationSec) => new[]
    {
        "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
        "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=" + durationSec,
        "-c:v", "libx264", "-preset", "ultrafast",
        "-pix_fmt", "yuv420p", "-g", "60",
        "-an",
        "-t", durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-f", "mpegts", outputPath,
    };

    private static async Task RunFfmpegAsync(string ffmpegPath, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stderrTask = p.StandardError.ReadToEndAsync();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        string stderr = await stderrTask;
        await stdoutTask;
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                "ffmpeg exit " + p.ExitCode + ": " + stderr);
    }
}
