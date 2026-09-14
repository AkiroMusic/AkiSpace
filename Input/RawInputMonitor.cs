using System.Runtime.InteropServices;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Input;

/// <summary>Relative mouse movement event payload.</summary>
public readonly record struct RelativeMouseMoveEventArgs(int DeltaX, int DeltaY, long TimestampTicks);

/// <summary>Contract for the raw-input relative mouse monitor.</summary>
public interface IRawInputMonitor
{
    /// <summary>Subscribes to relative mouse moves. Returns an IDisposable unsubscribe token.</summary>
    IDisposable Subscribe(Action<RelativeMouseMoveEventArgs> handler);

    /// <summary>Whether the monitor is currently running.</summary>
    bool IsRunning { get; }

    void Start();
    void Stop();
}

/// <summary>
/// Dedicated STA thread running a hidden-window message loop that captures
/// RAW relative mouse movement via RegisterRawInputDevices(RIDEV_INPUTSINK).
///
/// Must NOT run on the same thread as the RDP ActiveX control: the RDP control
/// registers its own raw input for the keyboard, and a conflicting registration
/// would override it (BetterGI's documented constraint). Reference-counted
/// lifecycle: starts on first subscriber, stops on last unsubscribe.
/// </summary>
public sealed class RawInputMonitor : IRawInputMonitor, IDisposable
{
    private readonly ILogger<RawInputMonitor> _logger;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    private Thread? _thread;
    private IntPtr _hwnd;  // protected by _hwndLock; volatile for inter-thread visibility
    private readonly object _hwndLock = new();
    private int _subscriberCount;
    private int _disposed;

    public RawInputMonitor(ILogger<RawInputMonitor> logger)
    {
        _logger = logger;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _thread != null && _thread.IsAlive; }
    }

    public IDisposable Subscribe(Action<RelativeMouseMoveEventArgs> handler)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var sub = new Subscription(this, handler);
            _subscriptions.Add(sub);
            _subscriberCount++;
            if (_subscriberCount == 1)
                StartCoreLocked();
            return sub;
        }
    }

    public void Start()
    {
        lock (_gate) StartCoreLocked();
    }

    public void Stop()
    {
        Thread? dying;
        lock (_gate) dying = StopCoreLocked();
        JoinStopped(dying);
    }

    private void StartCoreLocked()
    {
        if (_disposed != 0) return;
        if (_thread != null && _thread.IsAlive) return;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "AkiSpace.RawInputMonitor",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _logger.LogInformation("Raw input monitor thread started");
    }

    /// <summary>
    /// Posts WM_QUIT to the running monitor thread and clears the thread field.
    /// Returns the dying thread so callers can Join it OUTSIDE the lock — the
    /// raw-input handler takes <see cref="_gate"/> to snapshot subscriptions, so
    /// joining under the lock could deadlock against a dispatch in progress.
    /// </summary>
    private Thread? StopCoreLocked()
    {
        if (_thread == null || !_thread.IsAlive) return null;
        var dying = _thread;
        // Snapshot _hwnd under _hwndLock so a concurrent ThreadMain that is
        // mid-create doesn't have its handle zeroed out from under us before
        // we Post WM_QUIT. Capture the value to use after releasing the lock.
        IntPtr hwnd;
        lock (_hwndLock)
        {
            hwnd = _hwnd;
            // Clear the field so a quick restart doesn't try to PostMessage
            // to a window owned by the old (dying) thread.
            _hwnd = IntPtr.Zero;
        }
        if (hwnd != IntPtr.Zero)
        {
            User32.PostMessage(hwnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }
        _thread = null;
        _logger.LogInformation("Raw input monitor stopped");
        return dying;
    }

    private void JoinStopped(Thread? thread)
    {
        if (thread == null) return;
        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
            _logger.LogWarning("Raw input monitor thread did not exit within 2s of WM_QUIT");
        }
    }

    private void ThreadMain()
    {
        // An unhandled exception on this manual thread would terminate the whole
        // process (e.g. RegisterClass/CreateWindowEx failures throw from the
        // HiddenWindowHost constructor), so the entry point is a total backstop.
        try
        {
            ThreadMainCore();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Raw input monitor thread crashed");
        }
    }

    private void ThreadMainCore()
    {
        var hwndSource = new HiddenWindowHost(_logger);
        hwndSource.RawInputReceived += OnRawInputReceived;
        // Publish the new handle. On exit we only clear the field if it still
        // holds OUR handle — otherwise a quick Stop+Start race would let this
        // dying thread zero out the handle published by the replacement thread,
        // leaving the new monitor unstoppable (no WM_QUIT could be posted).
        var myHwnd = hwndSource.Handle;
        lock (_hwndLock) { _hwnd = myHwnd; }

        var device = new User32.RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // Generic Desktop
            usUsage = 0x02,     // Mouse
            dwFlags = InputConstants.RIDEV_INPUTSINK,
            hwndTarget = myHwnd,
        };
        var registered = User32.RegisterRawInputDevices(
            new[] { device }, 1, (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>());
        if (!registered)
        {
            _logger.LogError("RegisterRawInputDevices failed, Win32 error {Error}", Marshal.GetLastWin32Error());
            ClearPublishedHandle(myHwnd);
            hwndSource.Dispose();
            return;
        }
        _logger.LogDebug("Raw input device registered (mouse, INPUTSINK)");

        try
        {
            hwndSource.RunMessageLoop();
        }
        finally
        {
            // Unregister on exit. RIDEV_REMOVE requires a NULL hwndTarget to remove
            // all registrations for this usage page/usage (a non-NULL target makes
            // the call fail with ERROR_INVALID_PARAMETER on some configurations).
            var remove = new User32.RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x02,
                dwFlags = InputConstants.RIDEV_REMOVE,
                hwndTarget = IntPtr.Zero,
            };
            if (!User32.RegisterRawInputDevices(new[] { remove }, 1, (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>()))
            {
                _logger.LogDebug("Raw input unregister failed, Win32 error {Error}", Marshal.GetLastWin32Error());
            }
            ClearPublishedHandle(myHwnd);
            hwndSource.Dispose();
        }
    }

    private void ClearPublishedHandle(IntPtr myHwnd)
    {
        lock (_hwndLock)
        {
            if (_hwnd == myHwnd) _hwnd = IntPtr.Zero;
        }
    }

    private void OnRawInputReceived(IntPtr lParam)
    {
        try
        {
            uint size = 0;
            User32.GetRawInputData(lParam, InputConstants.RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<User32.RAWINPUTHEADER>());
            if (size == 0) return;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var written = User32.GetRawInputData(lParam, InputConstants.RID_INPUT, buffer, ref size, (uint)Marshal.SizeOf<User32.RAWINPUTHEADER>());
                if (written != size) return;

                var raw = Marshal.PtrToStructure<User32.RAWINPUT>(buffer);
                if (raw.header.dwType != InputConstants.RIM_TYPEMOUSE) return;
                if ((raw.mouse.usFlags & InputConstants.MOUSE_MOVE_ABSOLUTE) != 0) return;

                var deltaX = raw.mouse.lLastX;
                var deltaY = raw.mouse.lLastY;
                if (deltaX == 0 && deltaY == 0) return;

                var evt = new RelativeMouseMoveEventArgs(deltaX, deltaY, DateTime.UtcNow.Ticks);
                Subscription[]? snapshot = null;
                lock (_gate)
                {
                    if (_subscriptions.Count > 0)
                    {
                        snapshot = new Subscription[_subscriptions.Count];
                        _subscriptions.CopyTo(snapshot, 0);
                    }
                }
                if (snapshot != null)
                {
                    foreach (var sub in snapshot)
                        sub.Handler(evt);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Raw input dispatch error");
        }
    }

    private void Unsubscribe(Subscription sub)
    {
        Thread? dying;
        lock (_gate)
        {
            _subscriptions.Remove(sub);
            _subscriberCount--;
            dying = _subscriberCount <= 0 ? StopCoreLocked() : null;
        }
        JoinStopped(dying);
    }

    public void Dispose()
    {
        Thread? dying;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _subscriptions.Clear();
            _subscriberCount = 0;
            dying = StopCoreLocked();
        }
        JoinStopped(dying);
    }

    private sealed class Subscription : IDisposable
    {
        private RawInputMonitor? _owner;
        public Action<RelativeMouseMoveEventArgs> Handler { get; }
        public Subscription(RawInputMonitor owner, Action<RelativeMouseMoveEventArgs> handler)
        {
            _owner = owner;
            Handler = handler;
        }
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Unsubscribe(this);
        }
    }

    /// <summary>
    /// A minimal message-only hidden window host for the raw input thread.
    /// Uses a plain Win32 message loop (no WinForms dependency on this thread).
    /// </summary>
    private sealed class HiddenWindowHost : IDisposable
    {
        private readonly string _className;
        private readonly ILogger _logger;
        private IntPtr _hwnd;
        private IntPtr _module;
        private User32.WndProcDelegate _wndProcDelegate;

        public event Action<IntPtr>? RawInputReceived;

        public HiddenWindowHost(ILogger logger)
        {
            _logger = logger;
            _module = Marshal.GetHINSTANCE(typeof(HiddenWindowHost).Module);
            _className = "AkiSpaceRawInputWindow_" + Guid.NewGuid().ToString("N");

            var wndProc = new User32.WndProcDelegate(WndProc);
            _wndProcDelegate = wndProc;

            var wc = new User32.WNDCLASS
            {
                lpfnWndProc = wndProc,
                hInstance = _module,
                lpszClassName = _className,
            };
            if (User32.RegisterClass(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClass failed: {Marshal.GetLastWin32Error()}");

            _hwnd = User32.CreateWindowEx(
                0, _className, "AkiSpaceRawInput",
                0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        public IntPtr Handle => _hwnd;

        public void RunMessageLoop()
        {
            User32.MSG msg;
            while (true)
            {
                // GetMessage returns -1 on error (NOT a triple-state BOOL): dispatching
                // would then run on an uninitialized MSG, and looping on a bool cast
                // would spin forever. 0 = WM_QUIT, -1 = error — both end the loop.
                var result = User32.GetMessage(out msg, IntPtr.Zero, 0, 0);
                if (result == 0) break;
                if (result == -1)
                {
                    _logger.LogWarning("GetMessage failed, Win32 error {Error}; ending raw input loop", Marshal.GetLastWin32Error());
                    break;
                }
                User32.TranslateMessage(ref msg);
                User32.DispatchMessage(ref msg);
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == InputConstants.WM_INPUT)
            {
                RawInputReceived?.Invoke(lParam);
            }
            return User32.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                User32.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            User32.UnregisterClass(_className, _module);
        }
    }
}
