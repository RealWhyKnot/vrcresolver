using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using WKVRCProxy.Shared;

namespace WKVRCProxy.YtDlp;

// VRChat invokes its `Tools\yt-dlp.exe` for every video. PatchManager swaps
// VRChat's bundled vanilla yt-dlp for THIS binary at watchdog startup; the
// vanilla copy is preserved as `Tools\yt-dlp-og.exe` so we have an
// unconditional fallback target.
//
// Behaviour per invocation:
//   1. Extract the URL (first http(s) arg) and the `-f <selector>` value
//      from VRChat's argv. Infer player from `-f` (Unity caps height<=720;
//      anything else defaults to AVPro).
//   2. Connect to `\\.\pipe\WKVRCProxy.resolve` with a 1 s connect budget.
//   3. Send a v2 ResolveRequest as a single NDJSON line, including
//      vrchat_format_arg (raw -f value), accept_protocols / accept_codecs
//      defaults per the inferred player, and protocol_version=2.
//   4. Read one response line (10 s ceiling).
//      - action=resolved + url present  → write URL to stdout, exit 0.
//      - action=fallback_native (any reason) → exec sibling yt-dlp-og.exe
//        with the original argv, pipe stdout/stderr/exit-code through.
//      - any failure (no pipe, parse error, IO error, timeout, no URL field)
//        → exec sibling yt-dlp-og.exe (graceful degradation).
//
// Also forwards non-resolve invocations (e.g. `yt-dlp --version`,
// `yt-dlp --help`, anything without an http URL in argv) straight to
// yt-dlp-og.exe so VRChat's diagnostic / probe paths keep working.
//
// Stdout contract: VRChat's bundled yt-dlp writes the resolved URL on a
// single line terminated by exactly one '\n' — no CRLF, no BOM. We match
// that via raw Console.OpenStandardOutput().
[SupportedOSPlatform("windows")]
internal static class Program
{
    private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResolveDeadline = TimeSpan.FromSeconds(10);

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* best-effort */ }

        string url = ExtractUrl(args);
        string? formatArg = ExtractDashFValue(args);
        string player = InferPlayer(formatArg);

        // No URL in argv → not a resolve invocation. Forward straight to
        // vanilla yt-dlp so `--version`, `--help`, and any other diagnostic
        // probes VRChat (or a curious user) issues still work.
        if (string.IsNullOrEmpty(url))
            return await ExecFallbackAsync(args).ConfigureAwait(false);

        try
        {
            string? resolved = await ResolveOverPipeAsync(url, player, formatArg).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolved))
            {
                WriteUrlToStdout(resolved);
                return 0;
            }
        }
        catch
        {
            // Any unhandled exception inside the pipe path falls through
            // to the fallback exec. yt-dlp-wrapper.log captures the cause.
        }

        return await ExecFallbackAsync(args).ConfigureAwait(false);
    }

    // Returns the resolved stream URL on success, null on any failure
    // (caller falls back to yt-dlp-og.exe). Logs are best-effort to
    // <exeDir>\yt-dlp-wrapper.log — VRChat's Tools dir is the only
    // reliably-writable location for a child process at invocation time.
    private static async Task<string?> ResolveOverPipeAsync(string url, string player, string? formatArg)
    {
        using var ctsConnect = new CancellationTokenSource(PipeConnectTimeout);
        using var pipe = new NamedPipeClientStream(
            ".",
            WireConstants.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(ctsConnect.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Log("pipe connect timed out"); return null; }
        catch (Exception ex) { Log("pipe connect failed: " + ex.GetType().Name + ": " + ex.Message); return null; }

        using var ctsResolve = new CancellationTokenSource(ResolveDeadline);

        var req = new ResolveRequest
        {
            Action = WireConstants.ActionResolve,
            Id = Guid.NewGuid().ToString("N"),
            Url = url,
            Player = player,
            ProtocolVersion = WireConstants.ClientProtocolVersion,
            VrchatFormatArg = formatArg,
            AcceptProtocols = player == WireConstants.PlayerUnity
                ? WireConstants.UnityAcceptProtocols
                : WireConstants.AvProAcceptProtocols,
            AcceptCodecs = player == WireConstants.PlayerUnity
                ? WireConstants.UnityAcceptCodecs
                : WireConstants.AvProAcceptCodecs,
            MaxAudioChannels = player == WireConstants.PlayerUnity
                ? WireConstants.UnityMaxAudioChannels
                : WireConstants.AvProMaxAudioChannels,
        };

        byte[] payload;
        try { payload = JsonSerializer.SerializeToUtf8Bytes(req); }
        catch (Exception ex) { Log("request serialize failed: " + ex.Message); return null; }

        try
        {
            await pipe.WriteAsync(payload, ctsResolve.Token).ConfigureAwait(false);
            await pipe.WriteAsync(NewlineFrame, ctsResolve.Token).ConfigureAwait(false);
            await pipe.FlushAsync(ctsResolve.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { Log("pipe write failed: " + ex.Message); return null; }

        string? line;
        try { line = await ReadLineAsync(pipe, ctsResolve.Token).ConfigureAwait(false); }
        catch (Exception ex) { Log("pipe read failed: " + ex.Message); return null; }
        if (string.IsNullOrEmpty(line)) { Log("pipe returned empty response"); return null; }

        ResolveResponse? resp;
        try { resp = JsonSerializer.Deserialize<ResolveResponse>(line); }
        catch (Exception ex) { Log("response parse failed: " + ex.Message); return null; }
        if (resp == null) { Log("response was null"); return null; }

        if (resp.Action == WireConstants.ActionResolved && !string.IsNullOrEmpty(resp.Url))
        {
            Log("resolved id=" + resp.Id);
            return resp.Url;
        }

        if (resp.Action == WireConstants.ActionFallbackNative)
        {
            Log("fallback_native id=" + resp.Id + " reason=" + (resp.Reason ?? "?"));
            return null;
        }

        Log("unexpected response action=" + resp.Action);
        return null;
    }

    private static readonly byte[] NewlineFrame = new byte[] { (byte)'\n' };

    // Buffered NDJSON read. Mirrors LocalIpcServer's read loop so a single
    // response line is consumed up to the first '\n'; anything further on
    // the pipe is the server's responsibility (it shouldn't write more).
    private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        bool sawNewline = false;
        const int MaxResponseBytes = 4 * 1024 * 1024;
        while (ms.Length < MaxResponseBytes)
        {
            int n = await s.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) break;
            int consume = n;
            int nlIdx = Array.IndexOf(buf, (byte)'\n', 0, n);
            if (nlIdx >= 0) { sawNewline = true; consume = nlIdx; }
            for (int i = 0; i < consume && ms.Length < MaxResponseBytes; i++)
            {
                byte b = buf[i];
                if (b == (byte)'\r') continue;
                ms.WriteByte(b);
            }
            if (sawNewline) break;
        }
        if (ms.Length == 0) return null;
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    // Exec sibling yt-dlp-og.exe with the same argv. yt-dlp-og.exe lives
    // next to us — VRChat's Tools dir is our cwd when invoked, and
    // PatchManager preserves vanilla yt-dlp there as yt-dlp-og.exe before
    // installing this wrapper. If yt-dlp-og.exe is missing, we have nothing
    // to fall back to — print the original URL on stdout so VRChat's player
    // at least gets *a* URL to try.
    private static async Task<int> ExecFallbackAsync(string[] args)
    {
        string exeDir = AppContext.BaseDirectory;
        string ogPath = Path.Combine(exeDir, "yt-dlp-og.exe");
        if (!File.Exists(ogPath))
        {
            Log("FALLBACK: yt-dlp-og.exe missing — printing raw URL");
            string? raw = ExtractRawUrl(args);
            if (!string.IsNullOrEmpty(raw)) WriteUrlToStdout(raw);
            return 0;
        }

        Log("FALLBACK: exec yt-dlp-og.exe with " + args.Length + " args");
        var psi = new ProcessStartInfo
        {
            FileName = ogPath,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = exeDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) { Log("FALLBACK: Process.Start returned null"); return 1; }
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Log("FALLBACK: exec failed: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    // VRChat's stdout reader is line-buffered + picky: exactly one '\n'
    // terminator, no CRLF, no BOM. Don't use Console.WriteLine.
    private static void WriteUrlToStdout(string url)
    {
        string output = url.Trim() + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(output);
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }

    // First arg starting with http:// or https:// is the URL to resolve.
    // Quoted matches are stripped of surrounding quotes by the OS argv
    // parse before we see them.
    private static string ExtractUrl(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return a;
        }
        return "";
    }

    // Same logic as ExtractUrl, but returned as the "anything to print"
    // value when we fall through with no yt-dlp-og.exe available.
    private static string? ExtractRawUrl(string[] args) => ExtractUrl(args) is { Length: > 0 } u ? u : null;

    // Returns the value following "-f" or "--format", or null if absent.
    // Matches the form `-f <selector>` and `--format <selector>`; does NOT
    // attempt to handle `--format=<selector>` (VRChat uses the spaced form).
    private static string? ExtractDashFValue(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-f" || args[i] == "--format")
                return args[i + 1];
        }
        return null;
    }

    // Heuristic: VRChat caps Unity-player height at 720 in its `-f` selector;
    // AVPro typically allows up to 1080. Anything else (or a missing -f)
    // defaults to avpro since it's the more-capable codec set and the
    // server can downshift when needed.
    private static string InferPlayer(string? formatArg)
    {
        if (string.IsNullOrEmpty(formatArg)) return WireConstants.PlayerAvPro;
        if (formatArg.Contains("height<=720", StringComparison.OrdinalIgnoreCase))
            return WireConstants.PlayerUnity;
        return WireConstants.PlayerAvPro;
    }

    // Best-effort single-file diagnostic. VRChat's Tools dir is writable
    // by VRChat (and therefore by us when invoked as VRChat's child).
    // Failures are swallowed — a yt-dlp invocation that can't log shouldn't
    // break the resolve pipeline.
    private static void Log(string message)
    {
        try
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, "yt-dlp-wrapper.log");
            File.AppendAllText(logPath, "[" + DateTime.UtcNow.ToString("o") + "] " + message + "\n");
        }
        catch { /* best-effort */ }
    }
}
