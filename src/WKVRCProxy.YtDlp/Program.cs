using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
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
//   4. Read one response line (18 s ceiling — must outlast the watchdog's
//      LocalIpcServer per-request timeout so the synthesized fallback
//      response reliably wins the race).
//      - action=resolved + url present  → write URL to stdout, exit 0.
//      - action=fallback_native (any reason) → exec sibling yt-dlp-og.exe
//        with the original argv, pass through stdout/stderr/exit-code.
//      - any failure (no pipe, parse error, IO error, timeout, no URL field)
//        → same exec.
//
// Graceful-degradation contract: when the watchdog isn't running, when the
// server returns fallback_native, and when yt-dlp-og.exe is unavailable or
// crashes — VRChat must see the SAME behaviour it would see if WKVRCProxy
// weren't installed at all. yt-dlp-og.exe is the preserved vanilla copy;
// its stdout/stderr/exit-code is passed through unmodified. When even
// yt-dlp-og.exe is missing, we emit empty stdout + exit 0 so VRChat's
// resolver sees "no URL found" and falls into its own error path — never
// the original URL re-emitted as if we'd resolved to it.
//
// Stdout contract: VRChat's bundled yt-dlp writes the resolved URL on a
// single line terminated by exactly one '\n' — no CRLF, no BOM. We match
// that via raw Console.OpenStandardOutput().
//
// Logging: every invocation appends a start banner, a step-by-step trace,
// a fallback-stdout/stderr preview, and an END summary line to
// %LOCALAPPDATA%\WKVRCProxy\logs\yt-dlp-wrapper.log. Each line is prefixed
// with [<utc>] [<rid>] for grep correlation. URLs in argv are sanitized to
// host-only when logged (no path/query — they may carry tokens).
[SupportedOSPlatform("windows")]
internal static class Program
{
    private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResolveDeadline = TimeSpan.FromSeconds(18);

    // Per-invocation correlation id. 8 hex chars — enough to tell two
    // overlapping invocations apart when VRChat fires retries quickly.
    private static string s_rid = "????????";

    private static async Task<int> Main(string[] args)
    {
        // No Console.OutputEncoding setup here — the wrapper writes its
        // resolved URL via raw Console.OpenStandardOutput().Write(bytes)
        // and og fallback via the same raw write. Console.WriteLine is
        // never called. Setting OutputEncoding triggers SetConsoleOutputCP
        // syscalls + buffer flushing that just waste 1-3 ms per invocation.
        s_rid = Guid.NewGuid().ToString("N")[..8];
        var swTotal = Stopwatch.StartNew();

        try
        {
            string url = ExtractUrl(args);
            string? formatArg = ExtractDashFValue(args);
            string player = InferPlayer(formatArg);

            LogStartBanner(args, url, formatArg, player);

            int exitCode;
            string outcome;

            if (string.IsNullOrEmpty(url))
            {
                // No URL in argv -- not a resolve invocation (e.g.,
                // `yt-dlp --version`, `--help`, or a probe). Forward
                // straight to vanilla so diagnostic invocations work.
                Log("no URL in argv -- exec og fallback (diagnostic invocation)");
                await TrySendOgFallbackNotifyAsync(null, WireConstants.OgFallbackReasonNoUrlDiagnostic, swTotal.ElapsedMilliseconds).ConfigureAwait(false);
                exitCode = await ExecFallbackAsync(args).ConfigureAwait(false);
                outcome = "no-url-fallback";
            }
            else
            {
                (string? resolved, string? fallbackReason) result;
                try
                {
                    result = await ResolveOverPipeAsync(url, player, formatArg).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log("UNHANDLED in pipe path: " + ex.GetType().Name + ": " + ex.Message);
                    result = (null, WireConstants.OgFallbackReasonPipeResolveFailed);
                }

                if (!string.IsNullOrEmpty(result.resolved))
                {
                    WriteUrlToStdout(result.resolved);
                    Log("emitted resolved URL to stdout host=" + ExtractHost(result.resolved) + " bytes=" + result.resolved.Length);
                    exitCode = 0;
                    outcome = "pipe-resolved";
                }
                else
                {
                    // Notify the watchdog about the og fallback before
                    // invoking og so the watchdog console surfaces the
                    // fallback fact in real time. Fire-and-forget; the
                    // notify uses a fresh pipe connection that closes
                    // immediately, then we exec og normally.
                    string reason = result.fallbackReason ?? WireConstants.OgFallbackReasonPipeResolveFailed;
                    await TrySendOgFallbackNotifyAsync(url, reason, swTotal.ElapsedMilliseconds).ConfigureAwait(false);

                    exitCode = await ExecFallbackAsync(args).ConfigureAwait(false);
                    outcome = "pipe-failed-og-fallback";
                }
            }

            swTotal.Stop();
            Log("END exit=" + exitCode + " outcome=" + outcome + " elapsed_ms=" + swTotal.ElapsedMilliseconds);
            return exitCode;
        }
        finally
        {
            CloseLog();
        }
    }

    // Returns (resolvedUrl, null) on success, (null, reason) on any
    // failure (caller falls back to yt-dlp-og.exe and uses `reason` to
    // notify the watchdog). Every exit branch logs.
    private static async Task<(string? Url, string? FallbackReason)> ResolveOverPipeAsync(string url, string player, string? formatArg)
    {
        var swPipe = Stopwatch.StartNew();
        using var ctsConnect = new CancellationTokenSource(PipeConnectTimeout);
        using var pipe = new NamedPipeClientStream(
            ".",
            WireConstants.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ctsConnect.Token).ConfigureAwait(false);
            Log("pipe connect OK elapsed_ms=" + swPipe.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            Log("pipe connect TIMED OUT after " + swPipe.ElapsedMilliseconds + " ms (watchdog not running?)");
            return (null, WireConstants.OgFallbackReasonPipeConnectFailed);
        }
        catch (System.IO.FileNotFoundException)
        {
            Log("pipe connect ENOENT (watchdog not running)");
            return (null, WireConstants.OgFallbackReasonPipeConnectFailed);
        }
        catch (Exception ex)
        {
            Log("pipe connect failed: " + ex.GetType().Name + ": " + ex.Message);
            return (null, WireConstants.OgFallbackReasonPipeConnectFailed);
        }

        using var ctsResolve = new CancellationTokenSource(ResolveDeadline);

        string requestId = Guid.NewGuid().ToString("N");
        var req = new ResolveRequest
        {
            Action = WireConstants.ActionResolve,
            Id = requestId,
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
        try { payload = SerializeWithTrailingNewline(req); }
        catch (Exception ex) { Log("request serialize failed: " + ex.Message); return (null, WireConstants.OgFallbackReasonPipeResolveFailed); }

        var swSend = Stopwatch.StartNew();
        try
        {
            // Single WriteAsync containing payload + '\n'. Earlier impl
            // split this across two WriteAsync calls + an explicit
            // FlushAsync — three kernel transitions per pipe send. Named
            // pipes don't buffer like FileStream so the explicit Flush
            // was already redundant; the byte-mode pipe with PIPE_WAIT
            // dispatches the write atomically.
            await pipe.WriteAsync(payload, ctsResolve.Token).ConfigureAwait(false);
            // Subtract 1 from the logged byte count so the existing log
            // ("request sent ... bytes=...") keeps reporting the JSON
            // payload length and stays comparable across the change.
            Log("request sent id=" + requestId[..8] + " bytes=" + (payload.Length - 1) + " player=" + player + " elapsed_ms=" + swSend.ElapsedMilliseconds);
        }
        catch (Exception ex) { Log("pipe write failed: " + ex.GetType().Name + ": " + ex.Message); return (null, WireConstants.OgFallbackReasonPipeResolveFailed); }

        var swRead = Stopwatch.StartNew();
        string? line;
        try { line = await ReadLineAsync(pipe, ctsResolve.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Log("pipe read TIMED OUT after " + swRead.ElapsedMilliseconds + " ms (no terminal frame within " + (int)ResolveDeadline.TotalSeconds + " s)"); return (null, WireConstants.OgFallbackReasonPipeResolveFailed); }
        catch (Exception ex) { Log("pipe read failed: " + ex.GetType().Name + ": " + ex.Message); return (null, WireConstants.OgFallbackReasonPipeResolveFailed); }
        if (string.IsNullOrEmpty(line)) { Log("pipe returned empty response after " + swRead.ElapsedMilliseconds + " ms"); return (null, WireConstants.OgFallbackReasonPipeResolveFailed); }

        Log("response received bytes=" + line.Length + " elapsed_ms=" + swRead.ElapsedMilliseconds);

        ResolveResponse? resp;
        try { resp = JsonSerializer.Deserialize(line, WrapperJsonContext.Default.ResolveResponse); }
        catch (Exception ex) { Log("response parse failed: " + ex.GetType().Name + ": " + ex.Message); return (null, WireConstants.OgFallbackReasonPipeResolveFailed); }
        if (resp == null) { Log("response was null after deserialize"); return (null, WireConstants.OgFallbackReasonPipeResolveFailed); }

        if (resp.Action == WireConstants.ActionResolved && !string.IsNullOrEmpty(resp.Url))
        {
            Log("response action=resolved id=" + (resp.Id ?? "?")[..Math.Min(8, (resp.Id ?? "?").Length)] + " url-host=" + ExtractHost(resp.Url));
            return (resp.Url, null);
        }

        if (resp.Action == WireConstants.ActionFallbackNative)
        {
            Log("response action=fallback_native id=" + (resp.Id ?? "?")[..Math.Min(8, (resp.Id ?? "?").Length)] + " reason=" + (resp.Reason ?? "?"));
            return (null, WireConstants.OgFallbackReasonServerFallbackNative);
        }

        Log("response action UNKNOWN: " + resp.Action);
        return (null, WireConstants.OgFallbackReasonPipeResolveFailed);
    }

    // Serialize the request DTO with a trailing '\n' framing byte appended
    // in-place. Source-gen path (WrapperJsonContext) writes through a
    // PooledByteBufferWriter under the hood; we copy out and append the
    // newline so the wire send is one WriteAsync instead of two.
    private static byte[] SerializeWithTrailingNewline(ResolveRequest req)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(req, WrapperJsonContext.Default.ResolveRequest);
        byte[] framed = new byte[body.Length + 1];
        Buffer.BlockCopy(body, 0, framed, 0, body.Length);
        framed[body.Length] = (byte)'\n';
        return framed;
    }

    // v3.2: notify the watchdog that we're falling back to og.exe so it
    // can surface a single console line in real time. Fire-and-forget --
    // open a fresh pipe, write one JSON-NDJSON line, close. Any failure
    // (watchdog not running, pipe busy, etc.) is silently swallowed: og
    // fallback must never fail because diagnostic IPC failed. Tight
    // timeout (1.5 s) so a hung watchdog can't delay the og exec.
    private static async Task TrySendOgFallbackNotifyAsync(string? url, string reason, long elapsedMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            using var pipe = new NamedPipeClientStream(
                ".",
                WireConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(cts.Token).ConfigureAwait(false); }
            catch { return; /* watchdog not running -- og still runs, no diagnostic line */ }

            var notify = new WrapperEventNotify
            {
                Action = WireConstants.ActionOgFallbackNotify,
                Url = url,
                Reason = reason,
                ElapsedMs = elapsedMs,
                Rid = s_rid,
            };
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(notify, WrapperJsonContext.Default.WrapperEventNotify);
            byte[] framed = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, framed, 0, body.Length);
            framed[body.Length] = (byte)'\n';
            try { await pipe.WriteAsync(framed, cts.Token).ConfigureAwait(false); }
            catch { /* fire-and-forget */ }
        }
        catch { /* fire-and-forget */ }
    }

    // Buffered NDJSON read. Mirrors LocalIpcServer's read loop so a single
    // response line is consumed up to the first '\n'.
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
    // installing this wrapper. We capture og's stdout + stderr (so the log
    // sees what it actually emitted), then forward both to OUR own
    // stdout/stderr unmodified before exiting with og's exit code. VRChat's
    // pipe sees identical bytes to what it would see if vanilla yt-dlp had
    // been invoked directly — that's the graceful-degradation contract.
    //
    // When yt-dlp-og.exe is missing entirely, we emit EMPTY stdout + exit 0.
    // VRChat's resolver sees "no URL" and falls into its own error path,
    // matching what would happen if Tools dir were broken / WKVRCProxy
    // uninstalled mid-runtime. NEVER emit the input URL as if we'd resolved
    // it — that confuses VRChat into "resolved to same URL" and the player
    // tries to open a watch-page URL it can't decode.
    private static async Task<int> ExecFallbackAsync(string[] args)
    {
        string exeDir = AppContext.BaseDirectory;
        string ogPath = Path.Combine(exeDir, "yt-dlp-og.exe");
        if (!File.Exists(ogPath))
        {
            Log("FALLBACK no-og: yt-dlp-og.exe missing at " + ogPath + " — emitting empty stdout, exit 0");
            return 0;
        }

        Log("FALLBACK og: spawning " + ogPath + " with " + args.Length + " args");
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = ogPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = exeDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) { Log("FALLBACK og: Process.Start returned null"); return 0; }

            // Read both streams concurrently to avoid pipe-buffer deadlock
            // on yt-dlp invocations that emit non-trivial stderr (warnings,
            // bot-detect notices) while stdout is still streaming.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync().ConfigureAwait(false);
            string ogStdout = await stdoutTask.ConfigureAwait(false);
            string ogStderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            Log("FALLBACK og: exit=" + proc.ExitCode + " elapsed_ms=" + sw.ElapsedMilliseconds + " stdout_bytes=" + ogStdout.Length + " stderr_bytes=" + ogStderr.Length);
            if (ogStdout.Length > 0)
                Log("FALLBACK og stdout-preview: " + Preview(ogStdout, 240));
            if (ogStderr.Length > 0)
                Log("FALLBACK og stderr-preview: " + Preview(ogStderr, 240));

            // Pass through to our stdout/stderr so VRChat sees exactly what
            // vanilla yt-dlp would emit. Bytes copied verbatim — no encoding
            // mangling, no trailing-newline normalization, no BOM.
            if (ogStdout.Length > 0)
            {
                using var ourStdout = Console.OpenStandardOutput();
                byte[] bytes = Encoding.UTF8.GetBytes(ogStdout);
                ourStdout.Write(bytes, 0, bytes.Length);
                ourStdout.Flush();
            }
            if (ogStderr.Length > 0)
            {
                using var ourStderr = Console.OpenStandardError();
                byte[] bytes = Encoding.UTF8.GetBytes(ogStderr);
                ourStderr.Write(bytes, 0, bytes.Length);
                ourStderr.Flush();
            }

            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log("FALLBACK og: exec failed: " + ex.GetType().Name + ": " + ex.Message + " elapsed_ms=" + sw.ElapsedMilliseconds);
            // Graceful degradation — if og itself can't be spawned, give
            // VRChat empty stdout + exit 0 instead of bubbling the exception.
            return 0;
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

    // Bare host or "?" if the input isn't a parseable absolute URL. Used
    // for log lines where the full URL would carry user-identifying tokens
    // (YouTube video ids, twitch streams, etc.).
    private static string ExtractHost(string url)
    {
        if (string.IsNullOrEmpty(url)) return "?";
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u)) return u.Host;
        }
        catch { /* best-effort */ }
        return "?";
    }

    // Truncate + escape a free-form string for inclusion in a single log
    // line. Newlines are converted to literal "\n" so a multi-line yt-dlp
    // stderr block doesn't fragment the log.
    private static string Preview(string s, int maxLen)
    {
        string trimmed = s.Length > maxLen ? s[..maxLen] + "...(truncated)" : s;
        return trimmed.Replace("\r", "").Replace("\n", "\\n");
    }

    private static void LogStartBanner(string[] args, string url, string? formatArg, string player)
    {
        string ver = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
        var sb = new StringBuilder();
        sb.Append("START pid=").Append(Environment.ProcessId);
        sb.Append(" ver=").Append(ver);
        sb.Append(" argc=").Append(args.Length);
        sb.Append(" url-host=").Append(string.IsNullOrEmpty(url) ? "<none>" : ExtractHost(url));
        sb.Append(" player=").Append(player);
        sb.Append(" -f=").Append(formatArg ?? "<none>");
        // Args summary: drop any arg that's an absolute URL (host already
        // logged separately) and any arg that looks like a multi-K-char
        // host-allowlist (--exp-allow / --wild-allow) — those run into
        // thousands of chars and aren't useful in the per-line log.
        sb.Append(" flags=[");
        bool first = true;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                continue;
            if ((a == "--exp-allow" || a == "--wild-allow") && i + 1 < args.Length)
            {
                if (!first) sb.Append(',');
                int hostCount = args[i + 1].Split(',').Length;
                sb.Append(a).Append("[~").Append(hostCount).Append(" hosts]");
                i++;
                first = false;
                continue;
            }
            if (!first) sb.Append(',');
            sb.Append(a.Length > 64 ? a[..64] + "..." : a);
            first = false;
        }
        sb.Append(']');
        Log(sb.ToString());
    }

    // Best-effort single-file diagnostic. Log lands at
    //   %LOCALAPPDATA%Low\WKVRCProxy\logs\yt-dlp-wrapper.log
    // — must live under LocalLow because the wrapper runs at Low integrity
    // (inherited from VRChat's Tools dir which sits in LocalLow). A
    // Low-integrity process cannot write to Medium-integrity dirs, so the
    // earlier %LOCALAPPDATA% path silently failed for every VRChat-invoked
    // call. Watchdog reads from this same LocalLow path so log surfaces
    // are unified across components. Failures are still swallowed — a
    // yt-dlp invocation that can't log shouldn't break the resolve pipeline.
    //
    // Single FileStream cached for the lifetime of the invocation (~10-15
    // Log calls per resolve). Earlier impl re-opened the file on every call
    // via File.AppendAllText + Directory.CreateDirectory, costing 5-50 ms
    // of avoidable I/O per resolve. Lazy-init on first call so a wrapper
    // run that never logs (impossible today, but cheap to handle) doesn't
    // touch disk. CloseLog() is invoked from Main's finally so the stream
    // flushes before process exit; an exit that bypasses the finally still
    // produces a useful tail because we Flush after every WriteLine.
    private static readonly object s_logLock = new();
    private static StreamWriter? s_logWriter;
    private static bool s_logInitFailed;

    private static void Log(string message)
    {
        try
        {
            string line = "[" + DateTime.UtcNow.ToString("o") + "] [" + s_rid + "] " + message;
            lock (s_logLock)
            {
                var w = s_logWriter ?? OpenLogWriter();
                if (w == null) return;
                w.WriteLine(line);
                w.Flush();
            }
        }
        catch { /* best-effort */ }
    }

    private static StreamWriter? OpenLogWriter()
    {
        if (s_logInitFailed) return null;
        try
        {
            string logDir = WkvrcPaths.LogsDir();
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "yt-dlp-wrapper.log");
            var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            s_logWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = false, NewLine = "\n" };
            return s_logWriter;
        }
        catch
        {
            // Disk full / permissions / unexpected layout. Set the failure
            // flag so the next Log() doesn't keep retrying syscalls each
            // call — that's the exact loss the refactor is meant to avoid.
            s_logInitFailed = true;
            return null;
        }
    }

    private static void CloseLog()
    {
        lock (s_logLock)
        {
            try { s_logWriter?.Flush(); } catch { /* best-effort */ }
            try { s_logWriter?.Dispose(); } catch { /* best-effort */ }
            s_logWriter = null;
        }
    }
}
