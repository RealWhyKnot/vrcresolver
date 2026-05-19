using WKVRCProxy.Shared;

namespace WKVRCProxy;

// Outcome of a held window after the server has decided. Pull -> client
// uploads the held bytes. Drop -> client deletes them and sends a
// terminal result frame with phase="dropped". TtlExpired -> client
// hit its own hold timer before the server signalled; treated as Drop
// with reason=client_ttl_expired.
internal enum HelperWindowOutcome
{
    Pull,
    Drop,
    TtlExpired,
}

// Resolution of a window-ready announcement. UploadUrlOverride is non-null
// only on Pull when the server rotated the signed token; null = use the
// upload URL from the original lease frame. DropReason is non-null only on
// Drop and TtlExpired and carries the reason vocabulary value (cpu_won,
// peer_won, superseded, viewer_left, stream_quiesced, client_disconnected,
// invalid_metrics, client_ttl_expired).
internal readonly record struct HelperWindowResolution(
    HelperWindowOutcome Outcome,
    string? UploadUrlOverride,
    string? DropReason);

// The mesh-WS channel a HelperLeaseWorker uses to drive the window-pull
// handshake. Two operations: send a helper_window_ready frame to the
// server, and await the resulting helper_pull_window or helper_drop_window
// (or a TTL).
//
// MeshClient implements this against its persistent connection. A no-op
// stub implementation is used in unit tests that drive the worker without
// a real mesh socket.
internal interface IHelperLeaseChannel
{
    // True when both the server's welcome.features contains
    // "helper_window_pull" AND this client is configured to opt in via
    // helper_status.supports_window_pull. Worker gates on this before
    // entering the hold-and-announce path; false collapses to legacy
    // immediate-upload behaviour.
    bool WindowPullEnabled { get; }

    // Fire-and-forget the helper_window_ready frame. Worker awaits this
    // before parking on WaitForWindowResolutionAsync so the server has
    // the metrics in hand when it decides whether to pull or drop.
    Task SendWindowReadyAsync(HelperWindowReadyFrame frame, CancellationToken ct);

    // Park until the server sends helper_pull_window / helper_drop_window
    // for this lease, or the TTL elapses. Implementation: per-lease
    // TaskCompletionSource registered before the SendWindowReadyAsync call
    // so a same-tick reply from the server doesn't race the registration.
    Task<HelperWindowResolution> WaitForWindowResolutionAsync(
        string leaseId,
        TimeSpan ttl,
        CancellationToken ct);
}
