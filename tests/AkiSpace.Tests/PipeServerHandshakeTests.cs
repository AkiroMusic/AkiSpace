using System.IO.Pipes;
using AkiSpace.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Integration proof for the fail-closed pipe handshake (#2): a PipeServer with
/// NO nonce configured must reject every client; a server WITH a nonce must
/// reject wrong nonces and — as the positive control that proves these tests
/// can distinguish accept vs reject — accept the correct nonce.
///
/// ISOLATION: each test runs its server on a UNIQUE bare pipe name (PipeServer.PipeName
/// seam) and dials that same unique name. The production default is the machine-global
/// PipeNames.MouseForward; here we override it per-test so a prior test's OS pipe
/// instance (which the kernel frees asynchronously) can never collide with or steal a
/// connection from the next test. This removes the earlier drain+GC+sleep hack and the
/// flakiness it left behind. Both NamedPipeServerStream and NamedPipeClientStream
/// prepend "\\.\pipe\" themselves, so server and client must pass the identical BARE
/// string — which also proves the production server-full/client-bare class of mismatch
/// cannot recur: these tests exercise the exact same bare-name addressing the shipped
/// PipeServer/PipeClient use.
/// </summary>
public sealed class PipeServerHandshakeTests
{
    private static string UniquePipeName() => "AkiSpace_Test_" + Guid.NewGuid().ToString("N");

    [Fact(Timeout = 15000)]
    public async Task Server_WithoutNonce_Rejects_BogusHandshake()
    {
        var name = UniquePipeName();
        var server = new PipeServer(NullLogger<PipeServer>.Instance) { PipeName = name };
        // Deliberately NO SetNonce call — the server must fail closed.
        try
        {
            server.Start();
            using var client = await ConnectToListenerAsync(name);

            var bogus = new byte[32];
            new Random(42).NextBytes(bogus);
            await HandshakeAsync(client, bogus);

            // The server must tear the pipe down without accepting anything:
            //  - null frame (clean EOF)      => rejected (pass)
            //  - IOException (pipe destroyed)=> rejected (pass)
            //  - a returned frame            => accepted (FAIL)
            //  - still hanging after 3s      => accepted & kept open (FAIL)
            await AssertConnectionClosedWithoutAcceptanceAsync(client, server);
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    [Fact(Timeout = 15000)]
    public async Task Server_WithNonce_Rejects_WrongNonce()
    {
        var name = UniquePipeName();
        var server = new PipeServer(NullLogger<PipeServer>.Instance) { PipeName = name };
        var expected = new byte[32];
        new Random(1).NextBytes(expected);
        server.SetNonce(expected);
        try
        {
            server.Start();
            using var client = await ConnectToListenerAsync(name);

            var wrong = new byte[32];
            new Random(99).NextBytes(wrong); // right shape, wrong value
            await HandshakeAsync(client, wrong);

            await AssertConnectionClosedWithoutAcceptanceAsync(client, server);
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Positive control: with the correct nonce the handshake IS accepted. This
    /// proves the two rejection tests pass because of rejection, not because
    /// the connection can never complete for unrelated reasons. Also verifies
    /// the server sends a HandshakeAck(accepted) verdict frame.
    /// </summary>
    [Fact(Timeout = 25000)]
    public async Task Server_WithNonce_Accepts_CorrectNonce_AndSendsAck()
    {
        var name = UniquePipeName();
        var server = new PipeServer(NullLogger<PipeServer>.Instance) { PipeName = name };
        var nonce = new byte[32];
        new Random(7).NextBytes(nonce);
        server.SetNonce(nonce);
        PipeConnection? accepted = null;
        try
        {
            var connected = new TaskCompletionSource<PipeConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.ClientConnected += c => connected.TrySetResult(c);
            server.Start();
            using var client = await ConnectToListenerAsync(name);
            await HandshakeAsync(client, nonce);

            var winner = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(
                winner == connected.Task,
                "Correct nonce was NOT accepted — handshake verification is broken.");
            accepted = await connected.Task;

            Assert.True(server.IsClientConnected);

            // The verdict frame must arrive (order relative to the connected event is
            // unspecified, so poll briefly).
            var ack = await ReadFrameWithDeadlineAsync(client, TimeSpan.FromSeconds(5));
            Assert.NotNull(ack);
            Assert.Equal(IpcPayloadType.HandshakeAck, ack!.Value.Type);
            Assert.Equal(0x01, ack.Value.Payload[0]);
        }
        finally
        {
            if (accepted is not null) await accepted.DisposeAsync();
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// A client that connects and never sends the handshake must be dropped by the
    /// handshake timeout instead of wedging the single pipe instance forever; the
    /// server must then go on to accept a legitimate client.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Server_SilentClient_TimesOut_AndAcceptsNextClient()
    {
        var name = UniquePipeName();
        var server = new PipeServer(NullLogger<PipeServer>.Instance)
        {
            PipeName = name,
            HandshakeTimeout = TimeSpan.FromMilliseconds(500),
        };
        var nonce = new byte[32];
        new Random(3).NextBytes(nonce);
        server.SetNonce(nonce);
        PipeConnection? accepted = null;
        try
        {
            var connected = new TaskCompletionSource<PipeConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.ClientConnected += c => connected.TrySetResult(c);
            server.Start();

            // Silent client: connects, sends nothing, holds the pipe instance.
            using var silent = await ConnectToListenerAsync(name);

            // It must eventually be dropped (reject ack or EOF), not held forever.
            var verdict = await ReadFrameWithDeadlineAsync(silent, TimeSpan.FromSeconds(5));
            if (verdict is { } frame)
            {
                Assert.Equal(IpcPayloadType.HandshakeAck, frame.Type);
                Assert.Equal(0x00, frame.Payload[0]);
            }

            // And the single pipe instance must be free again for a real client.
            using var legit = await ConnectToListenerAsync(name);
            await HandshakeAsync(legit, nonce);
            var winner = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(winner == connected.Task,
                "Server did not accept a legitimate client after dropping a silent one.");
            accepted = await connected.Task;
            Assert.True(server.IsClientConnected);
        }
        finally
        {
            if (accepted is not null) await accepted.DisposeAsync();
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    // ---- helpers ----

    /// <summary>
    /// Dials the given bare pipe name, retrying until the (still starting) server
    /// instance exists. A unique name per test means the only server that can ever
    /// answer is THIS test's server.
    /// </summary>
    private static async Task<NamedPipeClientStream> ConnectToListenerAsync(string pipeName)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (true)
        {
            var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(2000);
                return client;
            }
            catch (Exception ex) when ((ex is TimeoutException or IOException) && DateTime.UtcNow < deadline)
            {
                client.Dispose();
                await Task.Delay(100);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Writes a Handshake frame. A rejection can arrive while the write is in
    /// flight (server disposes the pipe mid-write), surfacing as IOException —
    /// which is itself the rejection signal the callers then confirm.
    /// </summary>
    private static async Task HandshakeAsync(NamedPipeClientStream client, byte[] nonce)
    {
        try
        {
            await IpcProtocol.WriteFrameAsync(client, IpcPayloadType.Handshake, nonce);
        }
        catch (IOException)
        {
            // rejected at write time; the close-assertion below proves it
        }
    }

    /// <summary>
    /// Asserts the server closed the connection without accepting it: the only frame
    /// allowed back is the HandshakeAck(rejected) verdict; anything else, a clean EOF
    /// or broken pipe is also fine, and — if the read is still hanging after 3
    /// seconds — the server accepted the client, which violates fail-closed.
    /// </summary>
    private static async Task AssertConnectionClosedWithoutAcceptanceAsync(
        NamedPipeClientStream client, PipeServer server)
    {
        var readTask = IpcProtocol.ReadFrameAsync(client);
        if (await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(3))) != readTask)
            Assert.Fail("Fail-closed violated: server kept the connection open after an invalid handshake.");

        try
        {
            var frame = await readTask;
            if (frame is null) return; // clean EOF = rejection
            Assert.Equal(IpcPayloadType.HandshakeAck, frame.Value.Type);
            Assert.Equal(0x00, frame.Value.Payload[0]); // explicit reject verdict
        }
        catch (IOException)
        {
            // server destroyed the pipe = rejection. OK.
            // (EndOfStreamException derives from IOException, so it is covered here too.)
        }

        Assert.False(server.IsClientConnected);
    }

    /// <summary>Reads one frame or returns null when nothing arrives within the deadline.</summary>
    private static async Task<(IpcPayloadType Type, byte[] Payload)?> ReadFrameWithDeadlineAsync(
        NamedPipeClientStream client, TimeSpan deadline)
    {
        var readTask = IpcProtocol.ReadFrameAsync(client);
        var winner = await Task.WhenAny(readTask, Task.Delay(deadline));
        if (winner != readTask) return null;
        try
        {
            return await readTask;
        }
        catch (IOException)
        {
            return null; // pipe torn down = no frame
        }
    }
}
