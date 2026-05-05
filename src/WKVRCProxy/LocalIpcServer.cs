using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Named-pipe server at \\.\pipe\WKVRCProxy.resolve. The patched yt-dlp.exe
// connects, sends one ResolveRequest, reads one ResolveResponse, and closes.
//
// ACL: pipe is created with an explicit security descriptor that grants the
// current user full access (DACL) AND tags the pipe with a Low-integrity
// mandatory label (SACL `S:(ML;;NW;;;LW)`). Without the Low-integrity SACL,
// the wrapper deployed into VRChat's Tools dir (Low-integrity, inherited
// from the LocalLow path) can't connect — Windows MIC blocks the connect
// attempt before the DACL check fires. This was a silent bug for an entire
// session: VRChat invoked our wrapper, wrapper's pipe connect failed, wrapper
// silently fell through to og fallback. Mesh path bypassed entirely.
//
// Wire format on the pipe is newline-delimited JSON: client writes one
// request followed by '\n', server writes one response followed by '\n'.
// Newline framing keeps both sides simple — no length prefixes, no
// read-to-end hangs that would happen with raw stream deserialization.
//
// Per-connection budget is 15 s. On timeout/parse-error/MeshClient throwing
// we synthesize a fallback_native frame with the appropriate reason rather
// than dropping the connection, so the patched yt-dlp.exe always gets a
// definitive answer it can act on.
[SupportedOSPlatform("windows")]
internal sealed partial class LocalIpcServer : IDisposable
{
    // Per-request budget. Sized so the server has room to escalate from
    // its standard tier (yt-dlp:youtube-tv-combo, ~3-8 s) to a heavier
    // tier (browser-extract, vrchat-impersonate) without the watchdog
    // synthesizing a fallback_native too eagerly. The wrapper's read
    // budget (18 s) sits above this so the synthesized response always
    // wins the race when this timeout fires.
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(15);
    // Match the WS-side 4 MiB cap so a giant vrchat_format_arg (raw yt-dlp
    // -f selector) round-trips end-to-end. Pre-fix this was 64 KiB which
    // silently truncated large selectors mid-string; the resulting
    // truncated JSON failed to parse and surfaced as fallback_internal_error
    // with no diagnostic about WHY.
    private const int MaxRequestBytes = 4 * 1024 * 1024;

    private readonly MeshClient _mesh;
    private readonly CancellationTokenSource _cts = new();
    private Task? _accepter;

    public LocalIpcServer(MeshClient mesh)
    {
        _mesh = mesh;
    }

    public void Start()
    {
        _accepter = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_accepter != null)
        {
            try { await _accepter.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    // P/Invoke surface for creating the named pipe with both DACL and SACL
    // embedded in the SECURITY_DESCRIPTOR at CREATE time. The kernel
    // applies the SACL during creation without a SeSecurityPrivilege
    // check as long as the mandatory integrity level being set is at or
    // below the caller's — which is exactly our case (watchdog at Medium,
    // pipe label set to Low so the wrapper at Low can connect).
    //
    // Why not SetSecurityInfo post-create? Because the pipe handle returned
    // by CreateNamedPipe doesn't carry WRITE_OWNER access; SetSecurityInfo
    // with LABEL_SECURITY_INFORMATION fails with ACCESS_DENIED (5) on a
    // handle without WRITE_OWNER. The CREATE-time path bypasses this — the
    // kernel evaluates privilege at the create call rather than against
    // an open-handle access mask.
    //
    // Why not NamedPipeServerStreamAcl.Create with PipeSecurity carrying
    // a SACL via SetSecurityDescriptorSddlForm? The .NET path invokes a
    // SACL-modifying code branch that requires SeSecurityPrivilege — not
    // held by normal user processes. Direct P/Invoke avoids that path.

    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_READMODE_BYTE = 0x00000000;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint PIPE_UNLIMITED_INSTANCES = 255;
    private const uint NMPWAIT_USE_DEFAULT_WAIT = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafePipeHandle CreateNamedPipeW(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        IntPtr lpSecurityAttributes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl,
        int sddlRevision,
        out IntPtr secDesc,
        IntPtr secDescSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int SDDL_REVISION_1 = 1;
    private const string PipeSddl =
        // Owner = current user (filled in at runtime via {0}).
        // DACL: allow current user full pipe access (0x1f019f) + SYSTEM full
        //       access (so the kernel-level pipe namespace bookkeeping
        //       doesn't get denied).
        // SACL: mandatory integrity label LOW with NO_WRITE_UP policy. The
        //       label tags the object at Low integrity; NW (the standard
        //       policy flag) is required syntactically but with the level
        //       at Low it has no effect on Low+ processes.
        "O:{0}G:{0}D:(A;;0x1f019f;;;{0})(A;;0x1f019f;;;SY)S:(ML;;NW;;;LW)";

    private NamedPipeServerStream CreatePipeWithLowIntegrityLabel()
    {
        string ownerSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("could not resolve current user SID");
        string sddl = string.Format(PipeSddl, ownerSid);

        IntPtr secDesc = IntPtr.Zero;
        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, out secDesc, IntPtr.Zero) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "ConvertStringSecurityDescriptorToSecurityDescriptor failed");

        try
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = secDesc,
                bInheritHandle = 0,
            };
            IntPtr saPtr = Marshal.AllocHGlobal((int)sa.nLength);
            try
            {
                Marshal.StructureToPtr(sa, saPtr, false);
                string fullName = @"\\.\pipe\" + WireConstants.PipeName;
                var handle = CreateNamedPipeW(
                    fullName,
                    PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                    PIPE_UNLIMITED_INSTANCES,
                    nOutBufferSize: 0,
                    nInBufferSize: 0,
                    nDefaultTimeOut: NMPWAIT_USE_DEFAULT_WAIT,
                    lpSecurityAttributes: saPtr);
                if (handle.IsInvalid)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                        "CreateNamedPipeW failed");
                // Wrap the raw handle in NamedPipeServerStream. The
                // overload taking a SafePipeHandle expects the pipe to
                // already exist; isAsync=true matches the FILE_FLAG_OVERLAPPED
                // we passed in. isConnected=false because no client has
                // connected yet — the caller will WaitForConnectionAsync.
                return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
            }
            finally
            {
                Marshal.FreeHGlobal(saPtr);
            }
        }
        finally
        {
            LocalFree(secDesc);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipeWithLowIntegrityLabel();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ipc] could not create pipe instance: " + ex.Message);
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ipc] accept error: " + ex.Message);
                pipe.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleAsync(pipe, ct));
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken outerCt)
    {
        using var perReqCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        perReqCts.CancelAfter(PerRequestTimeout);
        string id = "";
        string? cid = null;
        var swReq = Stopwatch.StartNew();
        try
        {
            var (line, truncated) = await ReadLineAsync(pipe, perReqCts.Token).ConfigureAwait(false);
            if (truncated)
            {
                Console.WriteLine("[ipc] rejecting request: payload exceeded "
                    + MaxRequestBytes + " bytes without a newline terminator");
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            ResolveRequest? req = null;
            string? parseError = null;
            if (!string.IsNullOrWhiteSpace(line))
            {
                try { req = JsonSerializer.Deserialize<ResolveRequest>(line); }
                catch (Exception ex) { parseError = ex.GetType().Name + ": " + ex.Message; }
            }

            if (req == null || string.IsNullOrEmpty(req.Url))
            {
                // Surface parse failures + missing-url cases so a misbehaving
                // patched yt-dlp is diagnosable from the watchdog console.
                // Pre-fix this path was completely silent.
                if (parseError != null)
                {
                    Console.WriteLine("[ipc] request parse failed: "
                        + LogUtil.SanitizeForConsole(parseError, 160)
                        + " preview=" + LogUtil.SanitizeForConsole(line, 80));
                }
                else if (req != null)
                {
                    Console.WriteLine("[ipc] request missing url");
                }
                else
                {
                    Console.WriteLine("[ipc] empty request received");
                }
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            id = req.Id ?? "";
            cid = req.CorrelationId;

            // H12: validate action vocabulary. The DTO accepts any string;
            // a request with action="ping" or any non-resolve verb that
            // happens to also carry a url would otherwise be silently
            // forwarded to the mesh (which would reject — but with no
            // diagnostic on the watchdog side).
            if (!string.Equals(req.Action, WireConstants.ActionResolve, StringComparison.Ordinal))
            {
                Console.WriteLine("[ipc] rejecting request id=" + id +
                    " action=" + LogUtil.SanitizeForConsole(req.Action, 32) +
                    " — only \"resolve\" is accepted on this pipe");
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            // H11: validate player vocabulary. Server spec is case-sensitive
            // "avpro" | "unity"; anything else (including null/empty,
            // "AVPro", "AvPro") gets rejected here with a clear log line so
            // patched-yt-dlp casing drift surfaces in a bug report instead
            // of silently being routed to a server that will reject.
            if (req.Player != WireConstants.PlayerAvPro && req.Player != WireConstants.PlayerUnity)
            {
                Console.WriteLine("[ipc] rejecting request id=" + id + CidSuffix(cid) +
                    " player=" + LogUtil.SanitizeForConsole(req.Player ?? "<null>", 32) +
                    " — must be \"avpro\" or \"unity\" (case-sensitive)");
                await WriteFallbackAsync(pipe, id, WireConstants.FallbackInternalError, perReqCts.Token).ConfigureAwait(false);
                return;
            }

            // User-facing per-resolve summary — request line. Hostname only
            // (no path/query — token risk), player + target resolution.
            // Companion "response" line fires below at terminal-response
            // time so the operator sees both halves of every resolve.
            //
            // The `[via lh-yt]` tag fires when the user-pasted URL host is
            // localhost.youtube.com — the public-instance trust-list bypass
            // path. Surfaces at-a-glance whether the public-world workaround
            // is being exercised vs a direct-host paste. Same per-process
            // counter goes to the heartbeat line for aggregate visibility.
            string host = ExtractHost(req.Url);
            bool viaLhYt = IsLocalhostYoutubeUrl(req.Url);
            string playerLabel = FormatPlayerLabel(req);
            string requestLine = viaLhYt
                ? "  -> " + host + " [via lh-yt]  (" + playerLabel + ")"
                : "  -> " + host + "  (" + playerLabel + ")";
            WriteUserActivity(ConsoleColor.Cyan, requestLine);
            WatchdogStats.RecordResolve(viaLhYt);

            string? failReason = null;
            string outcome = "?";
            string? serverReason = null;
            try
            {
                // Lossless forward: hand the whole DTO to MeshClient so v2 fields
                // (protocol_version / accept_protocols / accept_codecs / etc.)
                // and any unknown fields populated by the patched yt-dlp pass
                // through to the mesh server unchanged. The DTO's
                // [JsonExtensionData] bag preserves anything we don't statically
                // know about.
                //
                // ResolveAsync returns the verified raw response bytes plus
                // the pre-extracted action and server-supplied reason. We
                // write the bytes straight to the pipe — no JsonDocument
                // re-encode on the hot path — and use the extracted strings
                // for the user-facing console summary.
                MeshResolveResult result = await _mesh.ResolveAsync(req, perReqCts.Token).ConfigureAwait(false);
                await WriteFrameAsync(pipe, result.Frame, perReqCts.Token).ConfigureAwait(false);
                outcome = result.Action;
                serverReason = result.Reason;
            }
            catch (OperationCanceledException)
            {
                failReason = WireConstants.FallbackServerUnreachable;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[ipc] mesh.ResolveAsync threw id=" + id + CidSuffix(cid) +
                    ": " + ex.GetType().Name + ": " +
                    LogUtil.SanitizeForConsole(ex.Message, 160));
                failReason = WireConstants.FallbackInternalError;
            }

            if (failReason != null)
            {
                outcome = WireConstants.ActionFallbackNative + "/" + failReason;
                await WriteFallbackAsync(pipe, id, failReason, CancellationToken.None).ConfigureAwait(false);
                ReportingService.ReportFallback(req, failReason, null);
            }
            else if (outcome.StartsWith(WireConstants.ActionFallbackNative))
            {
                // Mesh returned a fallback_native frame. Reach into the
                // dispatched response for the reason code; ReportingService
                // filters out transient kinds itself.
                string reason = outcome.Length > WireConstants.ActionFallbackNative.Length + 1
                    ? outcome[(WireConstants.ActionFallbackNative.Length + 1)..]
                    : "";
                if (!string.IsNullOrEmpty(reason))
                    ReportingService.ReportFallback(req, reason, null);
            }

            // User-facing per-resolve summary — terminal-response line.
            // Colour signals at-a-glance status: green = resolved, yellow =
            // server replied with fallback_native (we'll defer to og), red =
            // we synthesised fallback_native locally (server timeout / IPC
            // budget tripped). Pairs visually with the "->" request line.
            swReq.Stop();
            string elapsedLabel = FormatElapsed(swReq.Elapsed.TotalSeconds);
            ConsoleColor color;
            string symbolAndStatus;
            if (outcome == WireConstants.ActionResolved)
            {
                color = ConsoleColor.Green;
                symbolAndStatus = "OK resolved";
            }
            else if (failReason != null)
            {
                color = ConsoleColor.Red;
                symbolAndStatus = "XX failed (" + failReason + ")";
            }
            else if (outcome == WireConstants.ActionFallbackNative)
            {
                color = ConsoleColor.Yellow;
                string reason = !string.IsNullOrEmpty(serverReason) ? serverReason : "?";
                symbolAndStatus = "!! fallback (" + reason + ")";
            }
            else
            {
                color = ConsoleColor.DarkGray;
                symbolAndStatus = "?? " + outcome;
            }
            WriteUserActivity(color, "     " + symbolAndStatus + "  " + elapsedLabel);

            // Detailed per-request line (id, cid, full outcome) routed to
            // the rolling watchdog log only — kept off the user-facing
            // console window so the friendly summary above stays scannable.
            Logger.WriteFileOnly(
                "[ipc] resolve id=" + id + CidSuffix(cid) +
                " player=" + LogUtil.SanitizeForConsole(req.Player ?? WireConstants.PlayerUnknown, 16) +
                " outcome=" + LogUtil.SanitizeForConsole(outcome, 48) +
                " elapsed_ms=" + (long)swReq.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                "[ipc] connection error id=" + id + CidSuffix(cid) +
                ": " + ex.GetType().Name + ": " +
                LogUtil.SanitizeForConsole(ex.Message, 160));
        }
        finally
        {
            try { if (pipe.IsConnected) pipe.Disconnect(); } catch { /* ignore */ }
            pipe.Dispose();
        }
    }

    // " cid=<id>" suffix only when correlation_id is populated.
    private static string CidSuffix(string? correlationId) =>
        string.IsNullOrEmpty(correlationId) ? "" : " cid=" + LogUtil.SanitizeForConsole(correlationId, 64);

    // Append the NDJSON framing newline to a payload byte[] so the wire
    // send is one WriteAsync instead of two (payload + separate newline).
    // Named pipes (PIPE_TYPE_BYTE | PIPE_WAIT) dispatch the write atomically,
    // so coalescing also lets the caller drop the explicit FlushAsync that
    // used to follow the newline write.
    private static byte[] AppendNewline(byte[] payload)
    {
        byte[] framed = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, framed, 0, payload.Length);
        framed[payload.Length] = (byte)'\n';
        return framed;
    }

    // Returns the line, or null on empty connection. Sets `truncated` to
    // true if MaxRequestBytes was hit before a '\n' arrived — the caller
    // can then surface a "request_too_large" diagnostic instead of
    // confusing "malformed JSON" (which is what JsonSerializer would
    // report against a truncated payload).
    //
    // Buffered: one ReadAsync per 4 KiB chunk, then scan in-process for the
    // newline terminator. Pre-fix this read one byte per syscall — a 100 KiB
    // request needed 100k async syscalls.
    private static async Task<(string? Line, bool Truncated)> ReadLineAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        bool sawNewline = false;
        while (ms.Length < MaxRequestBytes)
        {
            int n = await s.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) break;
            int consume = n;
            int nlIdx = Array.IndexOf(buf, (byte)'\n', 0, n);
            if (nlIdx >= 0) { sawNewline = true; consume = nlIdx; }
            for (int i = 0; i < consume && ms.Length < MaxRequestBytes; i++)
            {
                byte b = buf[i];
                if (b == (byte)'\r') continue;
                ms.WriteByte(b);
            }
            if (sawNewline) break;
        }
        if (ms.Length == 0) return (null, false);
        bool truncated = !sawNewline && ms.Length >= MaxRequestBytes;
        return (Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length), truncated);
    }

    // Pass through pre-serialized JSON bytes from MeshClient. Appends the
    // NDJSON framing newline in-place and writes once. No JsonDocument
    // re-encode on the hot path — earlier impl took a JsonDocument and
    // called SerializeToUtf8Bytes(doc.RootElement) here, which re-emitted
    // the same JSON the dispatch handler had just parsed.
    private static async Task WriteFrameAsync(Stream s, byte[] frame, CancellationToken ct)
    {
        byte[] payload = AppendNewline(frame);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    // Skip null fields when serializing the synthetic fallback frame so the
    // wire shape stays v1-identical for v1 patched-yt-dlp consumers. Without
    // this, the v2 ResolveResponse fields (container, video_codec, etc.)
    // would each emit "field":null, forcing every fallback recipient to
    // tolerate keys it doesn't know.
    private static readonly JsonSerializerOptions FallbackSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task WriteFallbackAsync(Stream s, string id, string reason, CancellationToken ct)
    {
        var frame = new ResolveResponse
        {
            Action = WireConstants.ActionFallbackNative,
            Id = id,
            Reason = reason,
        };
        byte[] payload = AppendNewline(JsonSerializer.SerializeToUtf8Bytes(frame, FallbackSerializerOptions));
        try
        {
            await s.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        catch { /* peer may have hung up — we tried */ }
    }

    // True iff the URL's host is exactly `localhost.youtube.com`. Used for
    // the `[via lh-yt]` console tag and the heartbeat's via-lh-yt counter.
    // Match is exact (not substring); a longer host like
    // `notlocalhost.youtube.com` does NOT count.
    private static bool IsLocalhostYoutubeUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                return u.Host.Equals(HostsManager.MarkerHost, StringComparison.OrdinalIgnoreCase);
        }
        catch { /* best-effort */ }
        return false;
    }

    // Bare hostname (host minus optional "www." prefix) for the user-facing
    // per-resolve summary. Path / query are NEVER printed to console — they
    // can carry user-identifying tokens (YouTube video ids, twitch streams,
    // etc.). The full URL stays in the watchdog log file via Logger.
    private static string ExtractHost(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                string h = u.Host;
                if (h.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) h = h[4..];
                return h;
            }
        }
        catch { /* best-effort */ }
        return "?";
    }

    [GeneratedRegex(@"height<=(\d+)")]
    private static partial Regex HeightCapRegex();

    // Player + target resolution label for the request line. The wrapper
    // doesn't populate maxHeight today (the constraint lives in the
    // vrchat_format_arg's `[height<=N]` selector instead) so we parse that
    // when the explicit field is absent. Falls back to "max" when neither
    // is available.
    private static string FormatPlayerLabel(ResolveRequest req)
    {
        string player = req.Player == WireConstants.PlayerUnity ? "Unity" : "AVPro";
        if (req.MaxHeight is int mh && mh > 0)
            return player + " " + mh + "p";
        if (!string.IsNullOrEmpty(req.VrchatFormatArg))
        {
            var m = HeightCapRegex().Match(req.VrchatFormatArg);
            if (m.Success) return player + " " + m.Groups[1].Value + "p";
        }
        return player + " max";
    }

    // Compact elapsed-time label for the response line. Always one decimal
    // under 60 s so a 0.5 s cache hit is visible vs a 12.3 s server escalate;
    // switches to "<m>m<s>s" past 60 s for the unusual stuck case.
    private static string FormatElapsed(double seconds)
    {
        if (seconds < 60) return seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
        int m = (int)(seconds / 60);
        int s = (int)(seconds - m * 60);
        return m + "m" + s + "s";
    }

    // Atomic colour-set + line-write + colour-reset under a static lock so
    // concurrent resolves (a busy world spawning 10+ video players at once)
    // don't interleave their colour-state changes mid-line. Writes a local
    // HH:mm:ss timestamp at the head so the operator can eyeball the time
    // gap between request and response without parsing the file log.
    private static readonly object s_consoleLock = new();
    private static void WriteUserActivity(ConsoleColor color, string body)
    {
        string stamped = "[" + DateTime.Now.ToString("HH:mm:ss") + "]" + body;
        lock (s_consoleLock)
        {
            ConsoleColor prev;
            try { prev = Console.ForegroundColor; }
            catch { prev = ConsoleColor.Gray; }
            try
            {
                try { Console.ForegroundColor = color; } catch { /* no-tty */ }
                Console.WriteLine(stamped);
            }
            finally
            {
                try { Console.ForegroundColor = prev; } catch { /* no-tty */ }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
