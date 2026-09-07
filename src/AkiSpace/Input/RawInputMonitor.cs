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
        lock (_gate) StopCoreLocked();
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

    private void StopCoreLocked()
    {
        if (_thread == null || !_thread.IsAlive) return;
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
    }

    private void ThreadMain()
    {
        var hwndSource = new HiddenWindowHost();
        hwndSource.RawInputReceived += OnRawInputReceived;
        // Publish the new handle atomically with a memory barrier.
        lock (_hwndLock) { _hwnd = hwndSource.Handle; }

        var device = new User32.RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // Generic Desktop
            usUsage = 0x02,     // Mouse
            dwFlags = InputConstants.RIDEV_INPUTSINK,
            hwndTarget = hwndSource.Handle,
        };
        var registered = User32.RegisterRawInputDevices(
            new[] { device }, 1, (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>());
        if (!registered)
        {
            _logger.LogError("RegisterRawInputDevices failed, Win32 error {Error}", Marshal.GetLastWin32Error());
            lock (_hwndLock) { _hwnd = IntPtr.Zero; }
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
            // Unregister on exit
            var remove = new User32.RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x02,
                dwFlags = InputConstants.RIDEV_REMOVE,
                hwndTarget = hwndSource.Handle,
            };
            User32.RegisterRawInputDevices(new[] { remove }, 1, (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>());
            lock (_hwndLock) { _hwnd = IntPtr.Zero; }
            hwndSource.Dispose();
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
        lock (_gate)
        {
            _subscriptions.Remove(sub);
            _subscriberCount--;
            if (_subscriberCount <= 0)
                StopCoreLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _subscriptions.Clear();
            _subscriberCount = 0;
            StopCoreLocked();
        }
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
        private IntPtr _hwnd;
        private IntPtr _module;
        private User32.WndProcDelegate _wndProcDelegate;

        public event Action<IntPtr>? RawInputReceived;

        public HiddenWindowHost()
        {
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
            while (User32.GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
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