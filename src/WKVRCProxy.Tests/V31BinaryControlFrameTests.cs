using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using WKVRCProxy.Shared;
using Xunit;

namespace WKVRCProxy.Tests;

// Regression test for the 2026-05-05 user-incident reconnect-storm bug.
//
// Bug: client's binary dispatch only recognised the three v3.1 hot-path
// actions (resolved / fallback_native / resolve_log). Server emitted
// pong via SendTo<T> which msgpack-serialized for negotiated
// connections (server-side spec violation, separately fixed). Client
// hit the default branch and discarded with "unknown binary action".
// _lastPongUtc never advanced; HeartbeatLoopAsync hit PongDeadline and
// aborted the WS, producing a ~55 s reconnect cycle.
//
// Defense-in-depth: even with the server fixed, the client should
// gracefully tolerate ANY of the spec-control actions arriving on the
// binary path. Pong specifically must update _lastPongUtc so a
// hypothetical future server regression can't silently re-create the
// storm.
//
// Tests use reflection because DispatchBinaryFrameAsync is private +
// _lastPongUtc is a private field. InternalsVisibleTo grants access to
// `internal` members but not private; reflection is the minimal-source-
// disturbance way to lock the contract.
public class V31BinaryControlFrameTests
{
    private static byte[] EncodeSingleStringFrame(string action)
    {
        // Hand-craft a msgpack array [action] -- one-element array, single
        // string. Server's hot-path frames use [Key(N)] positional layout;
        // this matches what the binary dispatch expects (arrayHeader +
        // string action at index 0).
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(1);
        writer.Write(action);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static async Task DispatchBinaryAsync(WKVRCProxy.MeshClient client, byte[] payload)
    {
        var method = typeof(WKVRCProxy.MeshClient).GetMethod("DispatchBinaryFrameAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(client, new object[] { payload, CancellationToken.None })!;
        await task;
    }

    private static DateTime ReadLastPongUtc(WKVRCProxy.MeshClient client)
    {
        var field = typeof(WKVRCProxy.MeshClient).GetField("_lastPongUtc",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (DateTime)field!.GetValue(client)!;
    }

    [Fact]
    public async Task Binary_pong_advances_lastPongUtc()
    {
        var client = new WKVRCProxy.MeshClient();
        DateTime before = ReadLastPongUtc(client);

        byte[] payload = EncodeSingleStringFrame(WireConstants.ActionPong);
        await DispatchBinaryAsync(client, payload);

        DateTime after = ReadLastPongUtc(client);
        // Pre-fix this assertion failed -- pong hit the default branch
        // and _lastPongUtc stayed at DateTime.MinValue. Post-fix the
        // dedicated case branch updates it.
        Assert.True(after > before,
            "expected _lastPongUtc to advance after binary pong dispatch; got " + after);
    }

    [Fact]
    public async Task Binary_ping_does_not_throw()
    {
        // Ping arriving on the binary path should be handled (we send a
        // pong reply if the WS is open) without throwing. Without a live
        // WS the inner SendAsync is a no-op via the snapshot-and-check
        // gate; the test just confirms the dispatch path doesn't blow up.
        var client = new WKVRCProxy.MeshClient();
        byte[] payload = EncodeSingleStringFrame(WireConstants.ActionPing);
        await DispatchBinaryAsync(client, payload);
        // No throw == pass.
    }

    [Fact]
    public async Task Binary_unknown_action_still_falls_through_to_default()
    {
        // Sanity: we extended the recognised-action set but a TRULY
        // unknown action should still hit the default branch (logs warn,
        // returns). Test that it doesn't throw.
        byte[] payload = EncodeSingleStringFrame("frobnicate_widget");

        var client = new WKVRCProxy.MeshClient();
        await DispatchBinaryAsync(client, payload);
    }
}
