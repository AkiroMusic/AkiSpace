using AkiSpace.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Round-2 review P2-02 regressions, against REAL named pipes in-process:
/// the agent-side PipeClient must be able to reconnect after the pipe drops
/// (the server keeps listening), and an explicit nonce rejection must be
/// terminal (no retry) — retrying a stale nonce can never succeed.
/// </summary>
public sealed class PipeReconnectTests
{
    private static string UniquePipeName() => "AkiSpace_Test_" + Guid.NewGuid().ToString("N");

    private static byte[] Nonce(int seed)
    {
        var nonce = new byte[32];
        new Random(seed).NextBytes(nonce);
        return nonce;
    }

    [Fact(Timeout = 20000)]
    public async Task PipeClient_Reconnects_AfterServerSideDisconnect()
    {
        var name = UniquePipeName();
        var server = new PipeServer(NullLogger<PipeServer>.Instance) { PipeName = name };
        var client = new PipeClient(NullLogger<PipeClient>.Instance) { PipeName = name };
        var nonce = Nonce(11);
        server.SetNonce(nonce);
        client.SetNonce(nonce);

        try
        {
            var first = new TaskCompletionSource<PipeConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.ClientConnected += c => first.TrySetResult(c);
            server.Start();

            Assert.True(await client.ConnectAsync(), "first connect should succeed");
            var accepted = await first.Task; // server-side view of the session
            Assert.True(server.IsClientConnected);

            // Simulate a pipe drop (primary-side teardown): disposing the accepted
            // connection closes the stream; the client's read loop EOFs and flips
            // IsConnected to false.
            await accepted.DisposeAsync();
            await WaitForFalseAsync(() => client.IsConnected,
                "client never observed the disconnect");

            // Reconnect against the still-listening server with the SAME nonce.
            var second = new TaskCompletionSource<PipeConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.ClientConnected += c => second.TrySetResult(c);
            Assert.True(await client.ConnectAsync(), "reconnect should succeed");
            var winner = await Task.WhenAny(second.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(winner == second.Task, "server never accepted the reconnected client");
            Assert.True(server.IsClientConnected);
        }
        finally
        {
            await client.DisposeAsync();
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    [Fact(Timeout = 15000)]
    public async Task PipeClient_Reject_IsTerminal_NoRetry()
    {
        var name = UniquePipeName();
        var server = new PipeServer(NullLogger<PipeServer>.Instance) { PipeName = name };
        var client = new PipeClient(NullLogger<PipeClient>.Instance) { PipeName = name };
        server.SetNonce(Nonce(21)); // server expects A
        client.SetNonce(Nonce(22)); // client presents B — can never be accepted

        try
        {
            server.Start();
            var startedAt = Environment.TickCount64;
            // Terminal false, not an infinite retry loop: ConnectAsync returns as
            // soon as the reject verdict arrives.
            Assert.False(await client.ConnectAsync());
            Assert.True(Environment.TickCount64 - startedAt < 10_000,
                "ConnectAsync kept retrying a rejected nonce instead of giving up");
            Assert.False(server.IsClientConnected);
        }
        finally
        {
            await client.DisposeAsync();
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>Polls until the predicate turns false (bounded); fails the test otherwise.</summary>
    private static async Task WaitForFalseAsync(Func<bool> predicate, string failureMessage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.False(predicate(), failureMessage);
    }
}
