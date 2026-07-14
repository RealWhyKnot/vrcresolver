namespace VrcResolver.Shared;

// The product shipped as "WKVRCProxy" before the vrcresolver rename. A fleet
// of installs built under the old name is still live: old watchdogs hold the
// old mutex, old wrappers sit in VRChat's Tools dir and dial the old pipe,
// old updaters expect old file names in the payload, and old state dirs hold
// certs, caches, and settings. Every constant below exists so a pre-rename
// install keeps working while it crosses over. This file (plus the explicit
// migration/compat paths that consume it) is the only place in src where the
// old names may appear.
public static class LegacyCompat
{
    // Old single-instance mutex pair. The new watchdog acquires these IN
    // ADDITION to the new names so (a) a not-yet-updated portable install
    // can't run concurrently with a renamed one, and (b) the old updater,
    // which polls this mutex to confirm the watchdog is gone before
    // swapping files, still coordinates correctly during the transition.
    public const string LegacyWatchdogMutexName = "Global\\WKVRCProxy.Watchdog";
    public const string LegacyWatchdogMutexNameLocal = "Local\\WKVRCProxy.Watchdog";

    // Marker string baked into every wrapper binary shipped before the
    // rename. WrapperIdentity must keep recognizing those binaries as ours
    // (Tools-dir classification decides what is safe to replace or exec),
    // so the byte scan accepts this marker alongside the current one.
    public const string LegacyWrapperMarker =
        "WKVRCPROXY_WRAPPER_MARKER_v1:9b3e7c8a-7f23-4e6b-9c1d-a4f8e0d2c5b6";

    // Same marker as a UTF-8 span for byte scans. Must be a u8 literal so
    // the AOT compiler emits the bytes into rodata.
    public static ReadOnlySpan<byte> LegacyWrapperMarkerUtf8 =>
        "WKVRCPROXY_WRAPPER_MARKER_v1:9b3e7c8a-7f23-4e6b-9c1d-a4f8e0d2c5b6"u8;

    // PE ProductName stamped on pre-rename binaries. Second recognition
    // signal (belt and braces with the marker) for wrappers installed by
    // old builds.
    public const string LegacyProductName = "WKVRCProxy";

    // Directory name used by pre-rename state roots under LocalLow,
    // ProgramData, and the even-older LocalAppData location. Migration
    // reads from these; nothing writes to them anymore.
    public const string LegacyStateDirName = "WKVRCProxy";

    // Old named pipe. An old wrapper still installed in VRChat's Tools dir
    // keeps dialing this name until PatchManager swaps it for the new
    // build, so the watchdog serves BOTH pipe names during the transition.
    public const string LegacyPipeName = "WKVRCProxy.resolve";

    // Environment variables were prefixed WKVRCPROXY_ before the rename.
    // Reads go through this helper so a value set under the old prefix
    // keeps working -- a user's telemetry opt-out or terminal preference
    // must not silently reset because the prefix changed.
    private const string EnvPrefix = "VRCRESOLVER_";
    private const string LegacyEnvPrefix = "WKVRCPROXY_";

    public static string? GetEnvWithLegacyFallback(string suffix)
    {
        return Environment.GetEnvironmentVariable(EnvPrefix + suffix)
            ?? Environment.GetEnvironmentVariable(LegacyEnvPrefix + suffix);
    }
}
