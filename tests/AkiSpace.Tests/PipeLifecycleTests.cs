using System.Collections.Concurrent;
using System.Diagnostics;
using AkiSpace.Ipc;
using AkiSpace.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Round-3 review P1-02 / P2-01 / P2-04 regressions against REAL named pipes:
/// <list type="bullet">
/// <item>a connection that ends NATURALLY (peer EOF) must still release its stream —
/// the old single-flag design made DisposeAsync a no-op after RunAsync's finally,
/// leaking the OS pipe instance (server maxNumberOfServerInstances=1 → flaky reconnects);</item>
/// <item>the agent must discard queued-but-unreplayed batches when the connection
/// drops — stale motion must never inject across a reconnect, and its results must
/// never ride the new connection;</item>
/// <item>an agent whose primary stays unreachable must give up after a bounded
/// window instead of retrying forever as an orphan process.</item>
/// </list>
/// </summary>
public sealed class PipeLifecycleTests
{
    private static string UniquePipeName() => "AkiSpace_Test_" + Guid.NewGuid().ToString("N");

    private static byte[] Nonce(int seed)
    {
        var nonce = new byte[32];
        new Random(seed).NextBytes(nonce);
        return nonce;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan limit, string failureMessage)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(predicate(), failureMessage);
    }

    [Fact(Timeout = 25000)]
    public async Task NaturalEof_ReleasesServerStream_AndServerAcceptsNewClient()
    {
        var name = UniquePipeName();
        var server = new PipeServer(NullLogger<PipeServer>.Instance) { PipeName = name };
        var client = new PipeClient(NullLogger<PipeClient>.Instance) { PipeName = name };
        var nonce = Nonce(41);
        server.SetNonce(nonce);
        client.SetNonce(nonce);

        try
        {
            var accepted = new TaskCompletionSource<PipeConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.ClientConnected += c => accepted.TrySetResult(c);
            server.Start();

            Assert.True(await client.ConnectAsync());
            var serverConnection = await accepted.Task;
            Assert.True(server.IsClientConnected);
            Assert.False(serverConnection.IsStreamReleased, "stream released before the connection even ended");

            // The CLIENT closes its own transport — the server's read loop hits a
            // natural EOF. (Not a server-side DisposeAsync: that would release the
            // stream itself and mask the leak this test guards.)
            await client.DisposeAsync();

            await WaitUntilAsync(() => !server.IsClientConnected, TimeSpan.FromSeconds(5),
                "server never observed the client EOF");
            Assert.True(serverConnection.IsStreamReleased,
                "natural EOF did not release the server-side stream (pipe instance leak)");

            // With maxNumberOfServerInstances=1, the freed instance is exactly what
            // lets the next listener be created — a real client must reconnect.
            using var secondClient = new PipeClient(NullLogger<PipeClient>.Instance) { PipeName = name };
            secondClient.SetNonce(nonce);
            Assert.True(await secondClient.ConnectAsync(),
                "server could not accept a new client after a natural EOF (leaked pipe instance)");
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>In-memory logger the failure message can dump for post-mortem.</summary>
    private sealed class MemoryLogger : Microsoft.Extensions.Logging.ILogger<AgentRunner>
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }

    [Fact(Timeout = 30000)]
    public async Task AgentRunner_DiscardsQueuedBatches_WhenConnectionDrops()
    {
        var name = UniquePipeName();
        var nonce = Nonce(31);
        var log = new MemoryLogger();
        var server = new PipeServer(NullLogger<PipeServer>.Instance) { PipeName = name };
        server.SetNonce(nonce);
        var client = new PipeClient(NullLogger<PipeClient>.Instance) { PipeName = name };
        client.SetNonce(nonce);
        var agent = new AgentRunner(log, client)
        {
            ConnectGiveUpAfter = TimeSpan.FromSeconds(10),
        };

        var injected = 0L;
        var replayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The FIRST group blocks inside the hook until the test releases it — that
        // freezes replay mid-batch so the remaining batches pile up in the channel.
        agent.SendInputHook = (count, _) =>
        {
            Interlocked.Add(ref injected, count); // uint widens to long
            replayGate.Task.Wait(15_000);
            return count;
        };

        var runCts = new CancellationTokenSource();
        try
        {
            var accepted1 = new TaskCompletionSource<PipeConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.ClientConnected += c => accepted1.TrySetResult(c);
            server.Start();

            var runTask = agent.RunAsync(nonce, runCts.Token);
            var conn1 = await accepted1.Task.WaitAsync(TimeSpan.FromSeconds(8));

            // Give RunAsync's subscriptions time to land before the test's own
            // BatchReceived counter (subscription order is invocation order).
            await Task.Delay(200);

            // 10 batches, each with a "now"-anchored timestamp (replay pacing waits
            // until the sample's offset from BaseTicks — a far-future timestamp
            // would park the replay in Task.Delay for that long). The first batch's
            // group blocks in the hook, nine queue up behind it.
            var received = 0;
            client.BatchReceived += _ => Interlocked.Increment(ref received);
            for (var i = 0; i < 10; i++)
            {
                var baseTicks = Stopwatch.GetTimestamp();
                var batch = new RelativeMouseBatch
                {
                    FirstSequence = (ulong)i,
                    BaseTicks = baseTicks,
                    Samples = new[] { new RelativeMouseSample(5, 0, baseTicks) },
                };
                await conn1.SendAsync(IpcPayloadType.RelativeMouseBatch, IpcProtocol.SerializeBatch(batch));
            }
            await WaitUntilAsync(() => Volatile.Read(ref received) >= 10, TimeSpan.FromSeconds(5),
                "client never received all batches");
            await WaitUntilAsync(() => Volatile.Read(ref injected) >= 1, TimeSpan.FromSeconds(5),
                "replay never entered the blocking hook");

            // Primary-side connection death (natural EOF for the client). The agent
            // must drop the queued backlog on dequeue (pipe is down) — never inject
            // stale motion after reconnecting.
            await conn1.DisposeAsync();

            // Release the frozen group; the backlog (disconnected) is discarded on
            // dequeue, so nothing stale injects.
            agent.SendInputHook = (count, _) =>
            {
                Interlocked.Add(ref injected, count); // uint widens to long
                log.Lines.Enqueue($"HOOK count={count}");
                return count;
            };
            log.Lines.Enqueue("TEST releasing gate");
            replayGate.TrySetResult();
            await Task.Delay(500);
            Assert.Equal(1, Volatile.Read(ref injected));

            // The agent reconnects on its own; only NEW batches may inject. Send via
            // the SERVER-side view of the live connection (an accept event can carry
            // a stream the client's handshake-retry abandoned, so take the server's
            // CURRENT verified connection after the reconnect settles).
            await WaitUntilAsync(() => client.IsConnected, TimeSpan.FromSeconds(8),
                "agent never reconnected");
            var conn2 = server.CurrentConnectionForTest;
            Assert.NotNull(conn2);

            for (var i = 10; i < 12; i++)
            {
                var baseTicks = Stopwatch.GetTimestamp();
                var batch = new RelativeMouseBatch
                {
                    FirstSequence = (ulong)i,
                    BaseTicks = baseTicks,
                    Samples = new[] { new RelativeMouseSample(3, 0, baseTicks) },
                };
                await conn2.SendAsync(IpcPayloadType.RelativeMouseBatch, IpcProtocol.SerializeBatch(batch));
            }
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (Volatile.Read(ref injected) < 3 && DateTime.UtcNow < deadline)
                    await Task.Delay(50);
                if (Volatile.Read(ref injected) < 3)
                {
                    Assert.Fail(
                        $"new batches after reconnect never reached the replay (received={Volatile.Read(ref received)}, " +
                        $"injected={Volatile.Read(ref injected)}, clientConnected={client.IsConnected}, " +
                        $"runTaskCompleted={runTask.IsCompleted}, log=[{string.Join(" | ", log.Lines)}])");
                }
            }

            // 1 (frozen group) + 2 (new batches) — the 9 stale batches contributed 0.
            await Task.Delay(300);
            Assert.Equal(3, Volatile.Read(ref injected));

            runCts.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            runCts.Cancel();
            await client.DisposeAsync();
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    [Fact(Timeout = 25000)]
    public async Task AgentRunner_GivesUp_WhenPrimaryStaysUnreachable()
    {
        var name = UniquePipeName();
        var nonce = Nonce(51);
        var server = new PipeServer(NullLogger<PipeServer>.Instance) { PipeName = name };
        server.SetNonce(nonce);
        var client = new PipeClient(NullLogger<PipeClient>.Instance) { PipeName = name };
        client.SetNonce(nonce);
        var agent = new AgentRunner(NullLogger<AgentRunner>.Instance, client)
        {
            ConnectGiveUpAfter = TimeSpan.FromSeconds(2),
            SendInputHook = (count, _) => count,
        };

        try
        {
            var accepted = new TaskCompletionSource<PipeConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.ClientConnected += c => accepted.TrySetResult(c);
            server.Start();

            using var runCts = new CancellationTokenSource();
            var runTask = agent.RunAsync(nonce, runCts.Token);
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            // The primary "exits": the whole pipe server stops, nothing recreates it.
            await server.StopAsync();

            // The agent must exit on its own instead of retrying forever as an orphan.
            var finished = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(finished == runTask,
                "agent kept retrying an unreachable primary instead of giving up");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
