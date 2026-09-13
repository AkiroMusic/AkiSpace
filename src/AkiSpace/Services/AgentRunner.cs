using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AkiSpace.Ipc;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Services;

/// <summary>
/// Runs in the child (clone) session under <c>--agent --nonce-file &lt;path&gt;</c>.
/// Connects to the primary's named pipe, receives relative mouse batches,
/// replays them via SendInput, and sends back handled confirmations.
///
/// Batches are replayed through a single-threaded FIFO queue so results return
/// in receive order and every <see cref="PipeClient.SendResultAsync"/> is
/// awaited (no fire-and-forget). Motion timing is preserved by splitting each
/// batch into ~8ms sub-groups keyed off the sample timestamps and pacing the
/// SendInput calls with <see cref="Task.Delay"/> against an absolute schedule.
/// </summary>
public sealed class AgentRunner
{
    // Accumulated elapsed ticks that triggers flushing a SendInput sub-group.
    // Stopwatch.Frequency is a runtime-static (not a compile-time constant), so this must be static readonly.
    private static readonly long GroupThresholdTicks = Stopwatch.Frequency * 8 / 1000; // ~8ms

    private readonly ILogger<AgentRunner> _logger;
    private readonly PipeClient _pipeClient;

    // Bounded with DropOldest: replay paces itself to wall-clock time, so sustained
    // motion can enqueue faster than it drains. An unbounded channel would grow
    // memory without limit and stretch input latency toward minutes; dropping the
    // OLDEST backlog keeps the clone cursor near-real-time instead.
    private readonly Channel<RelativeMouseBatch> _batches =
        Channel.CreateBounded<RelativeMouseBatch>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    public AgentRunner(ILogger<AgentRunner> logger, PipeClient pipeClient)
    {
        _logger = logger;
        _pipeClient = pipeClient;
    }

    /// <summary>
    /// Connects to the primary pipe and runs the replay loop until cancelled.
    /// </summary>
    public async Task RunAsync(byte[] nonce, CancellationToken ct = default)
    {
        _pipeClient.SetNonce(nonce);
        _pipeClient.BatchReceived += OnBatchReceived;
        var consumer = ConsumeBatchesAsync(ct);
        try
        {
            await _pipeClient.ConnectAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Agent runner started — replaying mouse batches");

            // Keep alive until cancelled or disconnected.
            while (!ct.IsCancellationRequested && _pipeClient.IsConnected)
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent runner error");
        }
        finally
        {
            _pipeClient.BatchReceived -= OnBatchReceived;
            _batches.Writer.TryComplete();
            try { await consumer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Event handler: enqueues the batch (non-blocking, dropping the oldest backlog
    /// when saturated). The serialized consumer does the actual replay + result send,
    /// guaranteeing FIFO result ordering.
    /// </summary>
    private void OnBatchReceived(RelativeMouseBatch batch)
    {
        // TryWrite returns false only when a batch was dropped to admit this one
        // (DropOldest always makes room) — log, don't spam: the drop IS the policy.
        if (_batches.Writer.TryWrite(batch))
        {
            _logger.LogDebug("Queued batch {FirstSequence} ({Count} samples); queue depth {Depth}",
                batch.FirstSequence, batch.Samples.Length, _batches.Reader.Count);
        }
        else
        {
            _logger.LogDebug("Batch queue saturated handling {FirstSequence}; oldest backlog dropped", batch.FirstSequence);
        }
    }

    /// <summary>
    /// Single-threaded consumer: replays each batch, then awaits its result send,
    /// logging (never swallowing) any failure. Runs until cancelled or completed.
    /// </summary>
    private async Task ConsumeBatchesAsync(CancellationToken ct)
    {
        await foreach (var batch in _batches.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var handled = await ReplayBatchAsync(batch, ct).ConfigureAwait(false);
            try
            {
                // The wire field is LastSequence: cover every sample in the batch,
                // not just the first one. (Empty batches keep FirstSequence — a
                // length-1-minus-one wrap would report the previous batch's range.)
                var lastSequence = batch.Samples.Length > 0
                    ? batch.FirstSequence + (ulong)(batch.Samples.Length - 1)
                    : batch.FirstSequence;
                await _pipeClient.SendResultAsync(
                    new RelativeMouseResult(lastSequence, handled), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send mouse result for batch {Sequence}", batch.FirstSequence);
            }
        }
    }

    /// <summary>
    /// Replays one batch as paced SendInput sub-groups. Returns true on success,
    /// false on failure (so the caller sends Handled=false). Cancellation propagates.
    /// </summary>
    private async Task<bool> ReplayBatchAsync(RelativeMouseBatch batch, CancellationToken ct)
    {
        try
        {
            var replayStart = Stopwatch.GetTimestamp();
            var group = new List<User32.INPUT>();
            long groupTicks = 0; // first sample's elapsed ticks (relative to BaseTicks)

            foreach (var sample in batch.Samples)
            {
                if (sample.DeltaX == 0 && sample.DeltaY == 0) continue;

                var relTicks = sample.TimestampTicks - batch.BaseTicks;

                // Flush the current sub-group once it spans more than the threshold.
                if (group.Count > 0 && relTicks - groupTicks > GroupThresholdTicks)
                {
                    await FlushGroupAsync(group, replayStart, groupTicks, ct).ConfigureAwait(false);
                    group.Clear();
                    groupTicks = relTicks;
                }
                else if (group.Count == 0)
                {
                    groupTicks = relTicks;
                }

                group.Add(CreateMouseMove(sample.DeltaX, sample.DeltaY));
            }

            if (group.Count > 0)
            {
                await FlushGroupAsync(group, replayStart, groupTicks, ct).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to replay mouse batch {Sequence}", batch.FirstSequence);
            return false;
        }
    }

    /// <summary>
    /// Waits until the absolute schedule time for this sub-group, then emits a
    /// single SendInput(INPUT[]) for all its buffered moves.
    /// </summary>
    private async Task FlushGroupAsync(
        List<User32.INPUT> group, long replayStart, long relTicks, CancellationToken ct)
    {
        var targetMs = relTicks * 1000.0 / Stopwatch.Frequency;
        var elapsedMs = (Stopwatch.GetTimestamp() - replayStart) * 1000.0 / Stopwatch.Frequency;
        var delayMs = targetMs - elapsedMs;
        if (delayMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
        }

        var sent = User32.SendInput(
            (uint)group.Count, group.ToArray(), Marshal.SizeOf<User32.INPUT>());
        // SendInput returns the number of events actually injected. 0 means every
        // event was blocked (UIPI / an elevated window / input desktop mismatch) —
        // reporting Handled:true in that case would silently fake mouse input.
        if (sent != group.Count)
        {
            _logger.LogWarning(
                "SendInput injected {Sent}/{Total} mouse events (blocked events are typically UIPI/integrity related)",
                sent, group.Count);
        }
    }

    private static User32.INPUT CreateMouseMove(int deltaX, int deltaY) => new()
    {
        type = InputConstants.INPUT_MOUSE,
        U = new User32.InputUnion
        {
            mi = new User32.MOUSEINPUT
            {
                dx = deltaX,
                dy = deltaY,
                mouseData = 0,
                dwFlags = InputConstants.MOUSEEVENTF_MOVE,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };
}
