using System.Collections.Concurrent;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Input;

/// <summary>
/// Clips the cursor to the RDP viewer window and hides it while game-mouse
/// forwarding is active; restores on Alt-key release or deactivation.
/// Mirrors BetterGI's LocalCursorCapture.
///
/// All Win32 cursor work runs on ONE dedicated owner thread: ShowCursor's display
/// counter is per-thread, and Capture/Release arrive from arbitrary threadpool
/// timer threads — hiding on one thread and showing on another would leave the
/// cursor permanently invisible. ClipCursor/SetCursorPos would be safe anywhere,
/// but keeping the whole operation on the owner thread makes the pairing trivially
/// correct (FIFO order also guarantees Release runs after an in-flight Capture).
/// </summary>
public sealed class CursorCapture : IDisposable
{
    private readonly ILogger<CursorCapture> _logger;
    private readonly object _gate = new();
    private readonly BlockingCollection<Action> _ops = new(new ConcurrentQueue<Action>());
    private readonly Thread _ownerThread;
    private User32.RECT? _previousClipRect;
    private User32.RECT _captureBounds;
    private bool _isCapturing;
    private bool _failureWarned;
    private int _cursorHideCallCount;
    private int _disposed;

    public CursorCapture(ILogger<CursorCapture> logger)
    {
        _logger = logger;
        _ownerThread = new Thread(OpLoop)
        {
            IsBackground = true,
            Name = "AkiSpace.CursorOwner",
        };
        _ownerThread.Start();
    }

    public bool IsCapturing
    {
        get { lock (_gate) return _isCapturing; }
    }

    /// <summary>Runs an operation on the cursor-owner thread and waits for it.</summary>
    private void RunOnOwner(Action op)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var done = new ManualResetEventSlim(false);
        try
        {
            _ops.Add(() =>
            {
                try { op(); }
                finally { done.Set(); }
            });
            done.Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            done.Dispose();
        }
    }

    private void OpLoop()
    {
        foreach (var op in _ops.GetConsumingEnumerable())
        {
            try
            {
                op();
            }
            catch (Exception ex)
            {
                // A cursor op must never kill the owner thread.
                _logger.LogWarning(ex, "Cursor operation failed");
            }
        }
    }

    /// <summary>
    /// Clips the cursor to the given screen-space bounds and hides the cursor.
    /// Returns false if already capturing or the bounds are degenerate.
    /// </summary>
    /// <remarks>
    /// The caller's 5 ms poll retries a failed capture continuously, so failure
    /// logging is de-duplicated to once per failure streak (Warning on the first,
    /// silent afterwards) — an unlucky ClipCursor must not grow the log 200 lines
    /// per second. On failure no captured state is left behind.
    /// </remarks>
    public bool Capture(User32.RECT bounds)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        var result = false;
        RunOnOwner(() => result = CaptureCore(bounds));
        return result;
    }

    private bool CaptureCore(User32.RECT bounds)
    {
        lock (_gate)
        {
            if (_disposed != 0) return false;
            if (_isCapturing) return false;
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;

            // Save the previous clip rect so we can restore it later. Only trust the
            // captured RECT when GetClipCursor actually succeeded; otherwise leave null
            // so Release() falls through to ClipCursor(IntPtr.Zero) instead of restoring garbage.
            var gotClip = User32.GetClipCursor(out var previous);
            if (!gotClip)
            {
                WarnThrottled("GetClipCursor failed, error {Error}", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            _previousClipRect = gotClip ? previous : (User32.RECT?)null;
            _captureBounds = bounds;

            if (!User32.ClipCursor(ref bounds))
            {
                // Nothing was actually captured — discard the saved state so a later
                // Release() can't restore a rect we never clipped to.
                _previousClipRect = null;
                _captureBounds = default;
                WarnThrottled("ClipCursor failed, error {Error}", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return false;
            }
            _failureWarned = false;

            // Hide the cursor: ShowCursor(false) returns the previous display count;
            // loop until count goes negative (hidden), bounded to avoid runaway.
            _cursorHideCallCount = 0;
            for (var i = 0; i < 64; i++)
            {
                var count = User32.ShowCursor(false);
                _cursorHideCallCount++;
                if (count < 0) break;
            }

            _isCapturing = true;
            _logger.LogDebug("Cursor captured to {Bounds}", bounds);
            return true;
        }
    }

    /// <summary>First failure of a streak logs Warning; repeats stay silent.</summary>
    private void WarnThrottled(string message, int error)
    {
        if (_failureWarned)
        {
            _logger.LogDebug(message, error);
            return;
        }
        _failureWarned = true;
        _logger.LogWarning(message, error);
    }

    /// <summary>
    /// Temporarily releases the clip + restores cursor visibility (Alt key pressed),
    /// and moves the cursor to the center of the capture bounds.
    /// </summary>
    public void ReleaseTemporarily() => RunOnOwner(ReleaseTemporarilyCore);

    private void ReleaseTemporarilyCore()
    {
        lock (_gate)
        {
            if (!_isCapturing) return;

            User32.ClipCursor(IntPtr.Zero); // release restriction

            var centerX = _captureBounds.Left + _captureBounds.Width / 2;
            var centerY = _captureBounds.Top + _captureBounds.Height / 2;
            User32.SetCursorPos(centerX, centerY);

            RestoreCursorVisibilityLocked();
            _isCapturing = false;
            _logger.LogDebug("Cursor temporarily released (Alt)");
        }
    }

    /// <summary>Fully releases the clip and restores cursor visibility.</summary>
    public void Release() => RunOnOwner(ReleaseCore);

    private void ReleaseCore()
    {
        lock (_gate)
        {
            if (!_isCapturing && _cursorHideCallCount == 0) return;

            if (_previousClipRect is User32.RECT previous)
            {
                User32.ClipCursor(ref previous);
            }
            else
            {
                User32.ClipCursor(IntPtr.Zero);
            }

            RestoreCursorVisibilityLocked();
            _isCapturing = false;
            _previousClipRect = null;
            _logger.LogDebug("Cursor fully released");
        }
    }

    private void RestoreCursorVisibilityLocked()
    {
        for (var i = 0; i < _cursorHideCallCount; i++)
        {
            User32.ShowCursor(true);
        }
        _cursorHideCallCount = 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            // Drain the queue so a queued Release still runs, then stop the loop.
            _ops.CompleteAdding();
            if (!_ownerThread.Join(TimeSpan.FromSeconds(2)))
                _ownerThread.Interrupt();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cursor owner thread shutdown issue");
        }
    }
}
