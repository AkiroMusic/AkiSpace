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
    private bool _noAgentWarned;
    private PipeConnection? _primaryConnection;
    private System.Threading.Timer? _pollTimer;
    private int _disposed;

    /// <summary>
    /// Creates the forwarder. Call <see cref="Initialize"/> before enabling
    /// game-mouse mode.
    /// </summary>
    /// <remarks>
    /// Replay lives in <see cref="Services.AgentRunner"/> (child session). This class is
    /// the primary-session capture/forward engine; it does not consume pipe batches.
    /// </remarks>
    public MouseForwarder(
        ILogger<MouseForwarder> logger,
        IRawInputMonitor rawInputMonitor,
        CursorCapture cursorCapture,
        PipeServer pipeServer)
    {
        _logger = logger;
        _rawInputMonitor = rawInputMonitor;
        _cursorCapture = cursorCapture;
        _pipeServer = pipeServer;

        _pipeServer.ClientConnected += OnClientConnected;
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

    /// <summary>True when the replay agent (child side) is connected to the pipe.</summary>
    public bool IsAgentConnected
    {
        get { lock (_gate) return _primaryConnection != null && _primaryConnection.IsConnected; }
    }

    /// <summary>
    /// Enables or disables game-mouse forwarding. The pipe server keeps LISTENING in
    /// both states: stopping it would disconnect (and terminate) the already-running
    /// replay agent, and nothing re-launches the agent mid-session — an ON/OFF/ON
    /// cycle would then never forward again. Disabling only stops consuming input
    /// and releases the cursor; the agent connection stays alive across toggles.
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
                DisableCoreLocked();
            }
        }
    }

    /// <summary>Stops consuming and releases the cursor. Runs under <see cref="_gate"/>;
    /// does NOT check <see cref="_disposed"/> (Dispose calls this after setting it —
    /// an early return here would leak the subscription, timer and pipe server).</summary>
    private void DisableCoreLocked()
    {
        _subscription?.Dispose();
        _subscription = null;
        _pollTimer?.Dispose();
        _pollTimer = null;
        _forwardingActive = false;
        _accumulatedX = _accumulatedY = 0;
        _accumulationStartedAt = 0;
        _cursorCapture.Release();

        // Keep the pipe server listening so the agent survives toggles; the listener
        // handle is idle while the feature is off.
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // DisableCoreLocked deliberately does not check _disposed — this is the
            // path that must actually tear everything down (an early return would
            // leak the subscription, poll timer and pipe server).
            DisableCoreLocked();
            _pipeServer.ClientConnected -= OnClientConnected;
            _cursorCapture.Dispose();
        }
        // The pipe server's StopAsync is fire-and-forget by design; on process exit
        // the shell watchdog bounds the shutdown either way.
        _ = _pipeServer.StopAsync();
    }

    // ---------------------------------------------------------------- pipe wiring

    private void OnClientConnected(PipeConnection connection)
    {
        lock (_gate) _primaryConnection = connection;
        _logger.LogInformation("Mouse pipe client connected");
        connection.ResultReceived += OnResultReceived;
        connection.Disconnected += () =>
        {
            lock (_gate)
            {
                _primaryConnection = null;
                // Drop any in-flight accumulation — replaying stale deltas after a
                // reconnect would teleport the clone cursor.
                _accumulatedX = _accumulatedY = 0;
                _accumulationStartedAt = 0;
            }
            _cursorCapture.Release();
        };
    }

    private void OnResultReceived(RelativeMouseResult result)
    {
        // The agent reports per-batch injection success. A not-handled batch means
        // SendInput was blocked (UIPI/input desktop) — forwarding is futile while
        // that persists, so release the cursor instead of capturing input that
        // goes nowhere.
        if (!result.Handled)
        {
            _logger.LogWarning("Agent reported batch {Sequence} not handled; releasing cursor", result.LastSequence);
            _cursorCapture.Release();
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
        if (_primaryConnection == null || !_primaryConnection.IsConnected)
        {
            if (!_noAgentWarned)
            {
                _noAgentWarned = true;
                _logger.LogWarning(
                    "Mouse batch discarded: no agent connection (replay agent not running). " +
                    "Game mouse mode is ON but batches go nowhere.");
            }
            // Actually DISCARD the accumulation: keeping it would replay stale
            // deltas (a potentially huge cursor jump) once the agent reconnects.
            _accumulatedX = 0;
            _accumulatedY = 0;
            _accumulationStartedAt = 0;
            return;
        }
        _noAgentWarned = false;

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
            // All probing (key state, UI callbacks) happens OUTSIDE the lock: the raw
            // input thread takes the same lock per mouse event, and holding it across
            // GetAsyncKeyState / focus checks would stall input dispatch for the whole
            // probe. The locked section applies the resulting state transition — and
            // ALSO performs the capture, so a SetGameMouseModeEnabled(false) running
            // between the check and the capture cannot be undone by an in-flight tick
            // (mode-off would Release, then this tick would Capture again, leaving the
            // cursor stuck clipped+hidden with no timer left to release it).
            var altPressed = (User32.GetAsyncKeyState(InputConstants.VK_LMENU) & InputConstants.KEY_PRESSED) != 0 ||
                             (User32.GetAsyncKeyState(InputConstants.VK_RMENU) & InputConstants.KEY_PRESSED) != 0;
            bool rdpFocused = _isRdpFocused?.Invoke() == true;
            bool agentConnected = IsAgentConnected;
            // Forwarding requires a live agent: without one the input goes nowhere
            // while the cursor stays clipped+hidden on the host.
            bool shouldForward = rdpFocused && !altPressed && agentConnected;

            lock (_gate)
            {
                if (!_gameMouseModeEnabled) return;

                _altPressedMask = altPressed ? 1 : 0;

                if (shouldForward && !_forwardingActive)
                {
                    _forwardingActive = true;
                    _accumulatedX = _accumulatedY = 0;
                    _accumulationStartedAt = 0;
                }
                else if (!shouldForward && _forwardingActive)
                {
                    _forwardingActive = false;
                    if (altPressed)
                        _cursorCapture.ReleaseTemporarily();
                    else
                        _cursorCapture.Release();
                }

                if (!_forwardingActive) return;

                // Independent flush tick: the event-driven flush only fires on the
                // NEXT raw-input event, so a final partial batch would linger until
                // the mouse moves again. The 5 ms timer guarantees it goes out.
                var elapsedMs = (Stopwatch.GetTimestamp() - _accumulationStartedAt) * 1000.0 / Stopwatch.Frequency;
                if (_accumulationStartedAt != 0 && elapsedMs >= FlushInterval.TotalMilliseconds)
                {
                    FlushLocked();
                }

                var bounds = _getCaptureBounds?.Invoke() ?? default;
                if (bounds.Width > 0 && !_cursorCapture.IsCapturing)
                {
                    _cursorCapture.Capture(bounds);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PollCaptureState error");
        }
    }
}