using System.Collections.Concurrent;
using System.Text;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Input;

/// <summary>
/// Keyboard focus management for the RDP viewer: locates the ActiveX control's
/// hidden "Input Capture Window" and routes focus/system shortcuts there.
/// </summary>
public sealed class KeyboardHandler
{
    private readonly ILogger<KeyboardHandler> _logger;
    private readonly ConcurrentDictionary<IntPtr, IntPtr> _hostToInputWindow = new();

    public KeyboardHandler(ILogger<KeyboardHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Finds the "Input Capture Window" child of the RDP host and focuses it.
    /// Returns true if focus is confirmed on that window.
    /// </summary>
    public bool TryFocusRdpInputWindow(IntPtr rdpHostHandle)
    {
        var inputWindow = FindInputCaptureWindow(rdpHostHandle);
        if (inputWindow == IntPtr.Zero) return false;
        User32.SetFocus(inputWindow);
        return User32.GetFocus() == inputWindow;
    }

    /// <summary>Whether the RDP input capture window currently holds keyboard focus.</summary>
    public bool IsRdpInputWindowFocused(IntPtr rdpHostHandle)
    {
        var inputWindow = FindInputCaptureWindow(rdpHostHandle);
        return inputWindow != IntPtr.Zero && User32.GetFocus() == inputWindow;
    }

    /// <summary>Clears the cached input-window mapping (call after reconnect).</summary>
    public void ClearCache(IntPtr rdpHostHandle)
    {
        _hostToInputWindow.TryRemove(rdpHostHandle, out _);
    }

    private IntPtr FindInputCaptureWindow(IntPtr rdpHostHandle)
    {
        if (rdpHostHandle == IntPtr.Zero) return IntPtr.Zero;

        if (_hostToInputWindow.TryGetValue(rdpHostHandle, out var cached) &&
            cached != IntPtr.Zero && User32.IsWindow(cached))
        {
            return cached;
        }

        IntPtr found = IntPtr.Zero;
        User32.EnumChildWindows(rdpHostHandle, (hWnd, _) =>
        {
            var sb = new StringBuilder(256);
            User32.GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.ToString() == "Input Capture Window")
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
            _hostToInputWindow[rdpHostHandle] = found;
        else
            _logger.LogDebug("Input Capture Window not found for host {Host}", rdpHostHandle);

        return found;
    }
}