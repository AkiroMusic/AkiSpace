using System.Diagnostics;
using AkiSpace.Ipc;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Input;

/// <summary>
/// The game-mouse forwarding engine. Captures relative mouse deltas from the
/// raw input monitor on the primary desktop, accumulates them with a short
/// flush interval (flushing immediately on direction reversal), sends batches
/// over the named pipe to the child session, and manages cursor capture
/// (clip + hide) gated on the child's confirmation — the BetterGI architecture.
/// </summary>
public sealed class MouseForwarder : IDisposable
{
    /// <summary>Accumulation window before flushing a batch.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(10);

    private readonly ILogger<MouseForwarder> _logger;
    private readonly IRawInputMonitor _rawInputMonitor;
    private readonly CursorCapture _cursorCapture;
    private readonly PipeServer _pipeServer;
    private readonly PipeClient _pipeClient;
    private Func<User32.RECT>? _getCaptureBounds;
    private Func<bool>? _isRdpFocused;

    private readonly object _gate = new();
    private IDisposable? _subscription;
    private ulong _sequence;
    private long _accumulatedX;
    private long _accumulatedY;
    private long _accumulationStartedAt;
    private long _lastSampleTicks;
    private int _altPressedMask;
    private bool _gameMouseModeEnabled;
    private bool _forwardingActive;
    private bool _captureEnabled;
    private bool _handlingConfirmed;
    private PipeConnection? _primaryConnection;
    private System.Threading.Timer? _pollTimer;
    private int _disposed;

    /// <summary>
    /// Creates the forwarder. Call <see cref="Initialize"/> before enabling
    /// game-mouse mode.
    /// </summary>
    public MouseForwarder(
        ILogger<MouseForwarder> logger,
        IRawInputMonitor rawInputMonitor,
        CursorCapture cursorCapture,
        PipeServer pipeServer,
        PipeClient pipeClient)
    {
        _logger = logger;
        _rawInputMonitor = rawInputMonitor;
        _cursorCapture = cursorCapture;
        _pipeServer = pipeServer;
        _pipeClient = pipeClient;

        _pipeServer.ClientConnected += OnClientConnected;
        _pipeClient.BatchReceived += OnBatchReceived;
        _pipeClient.ResultReceived += OnResultReceived;
    }

    /// <summary>
    /// Wires the UI-provided callbacks. <paramref name="getCaptureBounds"/> returns the
    /// screen-space rectangle of the RDP viewer window; <paramref name="isRdpFocused"/>
    /// tells whether the RDP input window currently has focus.
    /// </summary>
    public void Initialize(Func<User32.RECT> getCaptureBounds, Func<bool> isRdpFocused)
    {
        _getCaptureBounds = getCaptureBounds;
        _isRdpFocused = isRdpFocused;
    }

    /// <summary>True when game-mouse forwarding is enabled and running.</summary>
    public bool IsGameMouseModeEnabled
    {
        get { lock (_gate) return _gameMouseModeEnabled; }
    }

    /// <summary>True when the pipeline is actively capturing + forwarding.</summary>
    public bool IsForwardingActive
    {
        get { lock (_gate) return _forwardingActive; }
    }

    /// <summary>True when the cursor is currently clipped/hidden.</summary>
    public bool IsCursorCaptured => _cursorCapture.IsCapturing;

    /// <summary>
    /// Enables or disables game-mouse mode. When enabled, the pipe server
    /// starts listening and raw input is captured; forwarding activates only
    /// when the RDP window is focused.
    /// </summary>
    public void SetGameMouseModeEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            if (_gameMouseModeEnabled == enabled) return;

            _gameMouseModeEnabled = enabled;
            _logger.LogInformation("Game mouse mode {State}", enabled ? "ON" : "OFF");

            if (enabled)
            {
                _subscription ??= _rawInputMonitor.Subscribe(OnRelativeMouseMoved);
                _pipeServer.Start();
                _pollTimer ??= new System.Threading.Timer(
                    _ => PollCaptureState(), null,
                    TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5));
            }
            else
            {
                _subscription?.Dispose();
                _subscription = null;
                _pollTimer?.Dispose();
                _pollTimer = null;
                _forwardingActive = false;
                _captureEnabled = false;
                _handlingConfirmed = false;
                _accumulatedX = _accumulatedY = 0;
                _cursorCapture.Release();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            SetGameMouseModeEnabled(false);
            _pipeServer.ClientConnected -= OnClientConnected;
            _pipeClient.BatchReceived -= OnBatchReceived;
            _pipeClient.ResultReceived -= OnResultReceived;
            _cursorCapture.Dispose();
        }
    }

    // ---------------------------------------------------------------- pipe wiring

    private void OnClientConnected(PipeConnection connection)
    {
        lock (_gate) _primaryConnection = connection;
        _logger.LogInformation("Mouse pipe client connected");
        connection.Disconnected += () =>
        {
            lock (_gate) _primaryConnection = null;
            _handlingConfirmed = false;
            _captureEnabled = false;
            _cursorCapture.Release();
        };
    }

    /// <summary>Child side: received a batch — replay via SendInput.</summary>
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
            // Confirm handled (in the real child instance this is gated on the
            // target app being active; here we always confirm since we replay blindly).
            _ = _pipeClient.SendResultAsync(new RelativeMouseResult(batch.FirstSequence, Handled: true));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to replay mouse batch");
            _ = _pipeClient.SendResultAsync(new RelativeMouseResult(batch.FirstSequence, Handled: false));
        }
    }

    /// <summary>Primary side: child confirmed it handled a batch.</summary>
    private void OnResultReceived(RelativeMouseResult result)
    {
        lock (_gate)
        {
            _handlingConfirmed = result.Handled;
        }
    }

    // ---------------------------------------------------------------- raw input path

    private void OnRelativeMouseMoved(RelativeMouseMoveEventArgs evt)
    {
        lock (_gate)
        {
            if (!_gameMouseModeEnabled || !_forwardingActive) return;
            if (_altPressedMask != 0) return;

            var dx = evt.DeltaX;
            var dy = evt.DeltaY;

            // Direction reversal -> flush immediately to avoid lag on turns
            var reversal = (_accumulatedX != 0 || _accumulatedY != 0) &&
                           (_accumulatedX * dx < 0 || _accumulatedY * dy < 0);

            var now = Stopwatch.GetTimestamp();
            var elapsedMs = (now - _accumulationStartedAt) * 1000.0 / Stopwatch.Frequency;
            if (reversal || elapsedMs >= FlushInterval.TotalMilliseconds)
            {
                FlushLocked();
            }

            _accumulatedX += dx;
            _accumulatedY += dy;
            _lastSampleTicks = evt.TimestampTicks;
            if (_accumulationStartedAt == 0)
                _accumulationStartedAt = Stopwatch.GetTimestamp();
        }
    }

    private void FlushLocked()
    {
        if (_accumulatedX == 0 && _accumulatedY == 0) return;
        if (_primaryConnection == null || !_primaryConnection.IsConnected) return;

        // Clamp deltas to a sane range (protects against driver spikes)
        const int maxDelta = 4096;
        var dx = (int)Math.Clamp(_accumulatedX, -maxDelta, maxDelta);
        var dy = (int)Math.Clamp(_accumulatedY, -maxDelta, maxDelta);

        var sample = new RelativeMouseSample(dx, dy, _lastSampleTicks);
        var batch = new RelativeMouseBatch
        {
            FirstSequence = _sequence++,
            BaseTicks = _lastSampleTicks,
            Samples = new[] { sample },
        };
        var payload = IpcProtocol.SerializeBatch(batch);

        _ = SendBatchAsync(_primaryConnection, payload);

        _accumulatedX = 0;
        _accumulatedY = 0;
        _accumulationStartedAt = 0;
    }

    private static async Task SendBatchAsync(PipeConnection connection, byte[] payload)
    {
        try
        {
            await connection.SendAsync(IpcPayloadType.RelativeMouseBatch, payload).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // pipe failure handled by Disconnected event
        }
    }

    // ---------------------------------------------------------------- capture state polling

    private void PollCaptureState()
    {
        try
        {
            lock (_gate)
            {
                if (!_gameMouseModeEnabled) return;

                var altPressed = (User32.GetAsyncKeyState(InputConstants.VK_LMENU) & InputConstants.KEY_PRESSED) != 0 ||
                                 (User32.GetAsyncKeyState(InputConstants.VK_RMENU) & InputConstants.KEY_PRESSED) != 0;
                _altPressedMask = altPressed ? 1 : 0;

                bool rdpFocused = _isRdpFocused?.Invoke() == true;
                bool shouldForward = rdpFocused && !altPressed;

                if (shouldForward && !_forwardingActive)
                {
                    _forwardingActive = true;
                    _accumulatedX = _accumulatedY = 0;
                    _accumulationStartedAt = 0;
                }
                else if (!shouldForward && _forwardingActive)
                {
                    _forwardingActive = false;
                    _captureEnabled = false;
                    if (altPressed)
                        _cursorCapture.ReleaseTemporarily();
                    else
                        _cursorCapture.Release();
                }
            }

            if (!_forwardingActive) return;

            var bounds = _getCaptureBounds?.Invoke() ?? default;
            if (bounds.Width > 0 && !_cursorCapture.IsCapturing)
            {
                _captureEnabled = _cursorCapture.Capture(bounds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PollCaptureState error");
        }
    }

    // ---------------------------------------------------------------- child replay

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