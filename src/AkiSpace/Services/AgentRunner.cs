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
    private readonly Channel<RelativeMouseBatch> _batches =
        Channel.CreateUnbounded<RelativeMouseBatch>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
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
    /// Event handler: enqueues the batch (non-blocking). The serialized consumer
    /// does the actual replay + result send, guaranteeing FIFO result ordering.
    /// </summary>
    private void OnBatchReceived(RelativeMouseBatch batch)
    {
        _batches.Writer.TryWrite(batch);
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
                await _pipeClient.SendResultAsync(
                    new RelativeMouseResult(batch.FirstSequence, handled), ct).ConfigureAwait(false);
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
    private static async Task FlushGroupAsync(
        List<User32.INPUT> group, long replayStart, long relTicks, CancellationToken ct)
    {
        var targetMs = relTicks * 1000.0 / Stopwatch.Frequency;
        var elapsedMs = (Stopwatch.GetTimestamp() - replayStart) * 1000.0 / Stopwatch.Frequency;
        var delayMs = targetMs - elapsedMs;
        if (delayMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
        }

        User32.SendInput(
            (uint)group.Count, group.ToArray(), Marshal.SizeOf<User32.INPUT>());
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
