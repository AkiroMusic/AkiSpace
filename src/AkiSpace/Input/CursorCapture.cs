using AkiSpace.Native;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Input;

/// <summary>
/// Clips the cursor to the RDP viewer window and hides it while game-mouse
/// forwarding is active; restores on Alt-key release or deactivation.
/// Mirrors BetterGI's LocalCursorCapture.
/// </summary>
public sealed class CursorCapture : IDisposable
{
    private readonly ILogger<CursorCapture> _logger;
    private readonly object _gate = new();
    private User32.RECT? _previousClipRect;
    private User32.RECT _captureBounds;
    private bool _isCapturing;
    private int _cursorHideCallCount;
    private int _disposed;

    public CursorCapture(ILogger<CursorCapture> logger)
    {
        _logger = logger;
    }

    public bool IsCapturing
    {
        get { lock (_gate) return _isCapturing; }
    }

    /// <summary>
    /// Clips the cursor to the given screen-space bounds and hides the cursor.
    /// Returns false if already capturing or the bounds are degenerate.
    /// </summary>
    public bool Capture(User32.RECT bounds)
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
                _logger.LogWarning("GetClipCursor failed, error {Error}", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            _previousClipRect = gotClip ? previous : (User32.RECT?)null;
            _captureBounds = bounds;

            if (!User32.ClipCursor(ref bounds))
            {
                _logger.LogWarning("ClipCursor failed, error {Error}", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return false;
            }

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

    /// <summary>
    /// Temporarily releases the clip + restores cursor visibility (Alt key pressed),
    /// and moves the cursor to the center of the capture bounds.
    /// </summary>
    public void ReleaseTemporarily()
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
    public void Release()
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

    /// <summary>
    /// Re-engages capture after a temporary Alt release (same bounds as last time).
    /// </summary>
    public bool Recapture()
    {
        lock (_gate)
        {
            if (_disposed != 0) return false;
            if (_isCapturing) return true;
            if (_captureBounds.Width <= 0 || _captureBounds.Height <= 0) return false;

            if (!User32.ClipCursor(ref _captureBounds))
            {
                _logger.LogWarning("Recapture ClipCursor failed, error {Error}", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return false;
            }
            _cursorHideCallCount = 0;
            for (var i = 0; i < 64; i++)
            {
                var count = User32.ShowCursor(false);
                _cursorHideCallCount++;
                if (count < 0) break;
            }
            _isCapturing = true;
            return true;
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
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Release();
        }
    }
}