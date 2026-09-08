using AkiSpace.Ipc;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Services;

/// <summary>
/// Runs in the child (clone) session under <c>--agent --nonce &lt;hex&gt;</c>.
/// Connects to the primary's named pipe, receives relative mouse batches,
/// replays them via SendInput, and sends back handled confirmations.
/// </summary>
public sealed class AgentRunner
{
    private readonly ILogger<AgentRunner> _logger;
    private readonly PipeClient _pipeClient;

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
        }
    }

    /// <summary>Replays a batch via SendInput and sends a result back.</summary>
    private void OnBatchReceived(RelativeMouseBatch batch)
    {
        try
        {
            foreach (var sample in batch.Samples)
            {
                if (sample.DeltaX != 0 || sample.DeltaY != 0)
                {
                    SendRelativeMouseMove(sample.DeltaX, sample.DeltaY);
                }
            }
            _ = _pipeClient.SendResultAsync(new RelativeMouseResult(batch.FirstSequence, Handled: true));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to replay mouse batch");
            _ = _pipeClient.SendResultAsync(new RelativeMouseResult(batch.FirstSequence, Handled: false));
        }
    }

    private static void SendRelativeMouseMove(int deltaX, int deltaY)
    {
        var input = new User32.INPUT
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
        User32.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<User32.INPUT>());
    }
}
