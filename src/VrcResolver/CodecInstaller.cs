using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using VrcResolver.Shared;

namespace VrcResolver;

// Silent video-codec auto-install. AVPro can't decode AV1 / HEVC / VP9
// without the matching Microsoft Store extension; on a fresh Windows or
// a locked-down corporate image those extensions are absent and AVPro
// fails with a generic decode error. Restoring the legacy startup hook
// so users with stripped Windows installs get the codecs without ever
// noticing.
//
// Behaviour:
//   - Fire-and-forget Task at boot (Run() returns immediately).
//   - Per-codec state cached at %LOCALAPPDATA%Low\vrcresolver\codec-state.json
//     so a successful install or a recent attempted-failed entry doesn't
//     re-trigger every launch.
//   - Each winget invocation has a 60 s budget; total budget is bounded
//     by sequential per-codec timeouts.
//   - Failures (winget missing, network outage, store auth) are silenced
//     to console.WriteLine (one line) so the operator sees what happened
//     without a stack trace dump.
[SupportedOSPlatform("windows")]
internal static class CodecInstaller
{
    private static readonly TimeSpan PerCodecTimeout = TimeSpan.FromSeconds(60);
    // If we attempted and failed, don't keep retrying every launch — back
    // off for a week. The user can manually clear codec-state.json to
    // force a retry sooner.
    private static readonly TimeSpan FailureRetryWindow = TimeSpan.FromDays(7);

    private sealed record Codec(string Name, string StoreId, string PackageFamilyName);

    private static readonly Codec[] Required =
    {
        new("AV1 Video Extension",   "9MVZQVXJBQ9V", "Microsoft.AV1VideoExtension"),
        new("HEVC Video Extension",  "9NMZLZ57R3T7", "Microsoft.HEVCVideoExtension"),
        new("VP9 Video Extensions",  "9N4D0MSV0403", "Microsoft.VP9VideoExtensions"),
    };

    public static void StartBackgroundCheck()
    {
        if (!AppSettingsStore.Shared.Snapshot().Maintenance.CodecAutoInstall)
        {
            Logger.WriteFileOnly("[codec] auto-install disabled by settings");
            return;
        }

        _ = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        try
        {
            var statePath = StatePath();
            var state = LoadState(statePath);
            bool dirty = false;

            foreach (var codec in Required)
            {
                if (ShouldSkip(state, codec.StoreId))
                    continue;

                bool installed = await TryEnsureInstalledAsync(codec).ConfigureAwait(false);
                state.Codecs[codec.StoreId] = new CodecEntry
                {
                    Status = installed ? "installed" : "failed",
                    LastAttemptUtc = DateTime.UtcNow,
                    PackageFamilyName = codec.PackageFamilyName,
                };
                dirty = true;
                if (installed)
                    ConsoleUx.Success(LogComponent.Codec, codec.Name + " ready.");
                else
                    ConsoleUx.Warn(LogComponent.Codec, codec.Name + " install failed; will retry next week.");
            }

            if (dirty) SaveState(statePath, state);
        }
        catch (Exception ex)
        {
            // Silenced to a single line. Codec install is non-critical;
            // a failure here must never propagate to the user as a stack.
            ConsoleUx.Warn(LogComponent.Codec, "background check failed: " + ex.Message);
        }
    }

    private static bool ShouldSkip(CodecState state, string storeId)
    {
        if (!state.Codecs.TryGetValue(storeId, out var entry)) return false;
        if (entry.Status == "installed") return true;
        if (entry.Status == "failed" && DateTime.UtcNow - entry.LastAttemptUtc < FailureRetryWindow) return true;
        return false;
    }

    private static async Task<bool> TryEnsureInstalledAsync(Codec codec)
    {
        // Fast-path: AppxPackage probe via PowerShell. If the package
        // is already present (e.g. shipped with the Windows image),
        // skip the winget install entirely.
        if (await IsAppxInstalledAsync(codec.PackageFamilyName).ConfigureAwait(false))
            return true;

        return await WingetInstallAsync(codec.StoreId).ConfigureAwait(false);
    }

    private static async Task<bool> IsAppxInstalledAsync(string packageFamilyName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList =
                {
                    "-NoProfile", "-ExecutionPolicy", "Bypass",
                    "-Command", "Get-AppxPackage -Name '" + packageFamilyName + "' | Select-Object -ExpandProperty Name",
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return false; }
            string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WingetInstallAsync(string storeId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                ArgumentList =
                {
                    "install", "--id", storeId, "--source", "msstore",
                    "--accept-package-agreements", "--accept-source-agreements",
                    "--silent",
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            using var cts = new CancellationTokenSource(PerCodecTimeout);
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* best-effort */ }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // winget not installed (older Windows, stripped image). One-line
            // diagnostic happens at the caller via the "install failed"
            // branch; no need to escalate here.
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string StatePath()
    {
        string dir = AppPaths.StateRoot();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "codec-state.json");
    }

    private static CodecState LoadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return new CodecState();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, MeshJsonContext.Default.CodecState) ?? new CodecState();
        }
        catch
        {
            return new CodecState();
        }
    }

    private static void SaveState(string path, CodecState state)
    {
        try
        {
            string tmp = path + ".new";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, MeshJsonContext.Default.CodecState));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort */ }
    }

    // AOT migration: promoted from private nested to internal nested so
    // MeshJsonContext can reference the types via [JsonSerializable]
    // and emit source-gen formatters.
    internal sealed class CodecState
    {
        [JsonPropertyName("codecs")]
        public Dictionary<string, CodecEntry> Codecs { get; set; } = new();
    }

    internal sealed class CodecEntry
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("last_attempt_utc")] public DateTime LastAttemptUtc { get; set; }
        [JsonPropertyName("package_family_name")] public string PackageFamilyName { get; set; } = "";
    }
}
