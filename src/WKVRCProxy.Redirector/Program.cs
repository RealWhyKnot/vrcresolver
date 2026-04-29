using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core;
using WKVRCProxy.Core.IPC;

namespace WKVRCProxy.Redirector;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Log to the same directory as the executable (VRChat Tools) — this is the
        // only location guaranteed to be writable when running as a VRChat child process.
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string logPath = Path.Combine(exeDir, "yt-dlp-wrapper.log");

        try
        {
            AppendLog(logPath, "Invoked with args: " + SummarizeArgs(args));

            // Look for port file: first in our own directory (VRChat Tools), then in AppData
            string portFile = Path.Combine(exeDir, "ipc_port.dat");

            if (!File.Exists(portFile))
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy");
                portFile = Path.Combine(appData, "ipc_port.dat");
            }

            if (!File.Exists(portFile))
            {
                AppendLog(logPath, "FAIL: ipc_port.dat not found in Tools or AppData — proxy not running?");
                return Fallback(args);
            }

            string portStr = File.ReadAllText(portFile).Trim();
            if (!int.TryParse(portStr, out int port))
            {
                AppendLog(logPath, "FAIL: ipc_port.dat corrupted (content: " + portStr + ")");
                return Fallback(args);
            }

            var payload = new ResolvePayload { Args = args };
            var envVars = Environment.GetEnvironmentVariables();
            foreach (System.Collections.DictionaryEntry de in envVars)
            {
                payload.Env[de.Key.ToString() ?? ""] = de.Value?.ToString() ?? "";
            }

            string json = JsonSerializer.Serialize(payload, CoreJsonContext.Default.ResolvePayload);

            using var ws = new ClientWebSocket();
            // 75 s covers Tier 2's 60 s cloud-resolver budget (AppConfig.Tier2TimeoutSeconds)
            // plus headroom for the cascade to fall through to Tier 3 if Tier 2 times out.
            // Old 15 s deadline killed the IPC call before Tier 2 could finish, so the wrapper
            // returned the original URL via Fallback() and AVPro tried to play raw YouTube /live/.
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75));

            try
            {
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/"), cts.Token);
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "FAIL: Could not connect to IPC on port " + port + " (" + ex.GetType().Name + ": " + ex.Message + ")");
                return Fallback(args);
            }

            AppendLog(logPath, "Connected to IPC on port " + port);

            var sendBytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(sendBytes), WebSocketMessageType.Text, true, cts.Token);

            // Chunked read response
            var buffer = new byte[1024 * 16];
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string responseBase64 = Encoding.UTF8.GetString(ms.ToArray());
            if (!string.IsNullOrEmpty(responseBase64))
            {
                string finalUrl = Encoding.UTF8.GetString(Convert.FromBase64String(responseBase64));
                AppendLog(logPath, "Resolved: " + finalUrl.Substring(0, Math.Min(100, finalUrl.Length)));
                WriteToStdout(finalUrl);
                return 0;
            }

            AppendLog(logPath, "FAIL: IPC returned empty response");
            return Fallback(args);
        }
        catch (Exception ex)
        {
            AppendLog(logPath, "FAIL: " + ex.GetType().Name + ": " + ex.Message);
            return Fallback(args);
        }
    }

    private static int Fallback(string[] args)
    {
        string? url = args.FirstOrDefault(a => a.StartsWith("http"));
        if (url != null) WriteToStdout(url);
        return 0;
    }

    private static void AppendLog(string path, string message)
    {
        try
        {
            File.AppendAllText(path, "[" + DateTime.Now.ToString("s") + "] " + message + "\n");
        }
        catch { /* Can't log â€” don't crash */ }
    }

    // VRChat passes static allow-lists (--exp-allow / --wild-allow) that run to thousands of
    // characters and never change between calls. Replace those values with a count summary so
    // the log line stays scannable while still showing the salient args (--get-url, -f, etc.).
    private static string SummarizeArgs(string[] args)
    {
        var parts = new System.Collections.Generic.List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if ((a == "--exp-allow" || a == "--wild-allow") && i + 1 < args.Length)
            {
                int hostCount = args[i + 1].Split(',').Length;
                parts.Add(a);
                parts.Add("[" + hostCount + " hosts]");
                i++;
                continue;
            }
            parts.Add(a);
        }
        return string.Join(" | ", parts);
    }

    private static void WriteToStdout(string result)
    {
        string output = result.Trim() + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(output);
        using (Stream stdout = Console.OpenStandardOutput())
        {
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
    }
}
