using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Controls;

/// <summary>
/// WinForms AxHost wrapper around the MSTSC ActiveX control (MsRdpClient10).
/// Connects to the local Windows child session using the "ConnectToChildSession"
/// extended property — the exact technique BetterGI uses for its desktop clone.
///
/// COM properties are accessed via reflection (late binding) to avoid a
/// compile-time dependency on the MSTSCLib interop assembly.
/// </summary>
public sealed class RdpActiveXHost : AxHost
{
    private const string Clsid = RdpCom.RdpClient11Clsid;

    private readonly ILogger<RdpActiveXHost> _logger;
    private readonly object _ocxGate = new();
    private IntPtr? _inputWindow;
    private volatile bool _connecting;
    private CancellationTokenSource? _connectCts;
    private AxHost.ConnectionPointCookie? _eventCookie;
    private RdpEventSink? _eventSink;

    // Last-attempt diagnostic info (captured at connect time, surfaced in the
    // ConnectionFailed message so the user sees the real failure without needing
    // DebugView). Reset on every TryConnectCore attempt.
    private bool _lastElevated;
    private string? _lastUser;
    private bool? _lastConnectToChildSessionReadback;
    // WTS child-session broker state captured right before the connect attempt.
    // BetterGI's ChildSessionService.StartAsync calls EnsureChildSessionsEnabled
    // (which calls WTSEnableChildSessions(true) + WTSIsChildSessionsEnabled) right
    // before ConnectCore. We do the same here so the broker is provably active in
    // the current process context before we ask mstscax to connect.
    private bool _lastWtsEnableOk;
    private int _lastWtsEnableErr;
    private bool _lastWtsIsEnabled;
    private int _lastWtsIsEnabledErr;

    public RdpActiveXHost(ILogger<RdpActiveXHost> logger) : base(Clsid)
    {
        _logger = logger;
        Dock = DockStyle.Fill;
    }

    // ---------------------------------------------------------------- event sink wiring

    /// <summary>
    /// AxHost calls this when the control's events should start. The default
    /// implementation is a no-op; we must explicitly advise our
    /// IMsTscAxEvents sink to the ActiveX control's connection point,
    /// otherwise all RDP events are silently dropped.
    /// </summary>
    protected override void CreateSink()
    {
        base.CreateSink();
        try
        {
            var ocx = GetOcx();
            if (ocx == null) return;
            _eventSink = new RdpEventSink(this);
            _eventCookie = new ConnectionPointCookie(ocx, _eventSink, typeof(RdpCom.IMsTscAxEvents));
            _logger.LogInformation("RDP event sink advised");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to advise RDP event sink");
        }
    }

    protected override void DetachSink()
    {
        try { _eventCookie?.Disconnect(); } catch { /* ignore */ }
        _eventCookie = null;
        _eventSink = null;
        base.DetachSink();
    }

    /// <summary>Raised when the RDP login completes (child session desktop ready).</summary>
    public event Action? LoginCompleted;

    /// <summary>Raised when the connection fails or is terminated. Argument = reason string.</summary>
    public event Action<string>? ConnectionFailed;

    /// <summary>Raised when the remote requests fullscreen / leaves fullscreen.</summary>
    public event Action? RequestedGoFullScreen;
    public event Action? RequestedLeaveFullScreen;

    /// <summary>Raised when the remote session signals focus was released (Ctrl+Alt+arrow).</summary>
    public event Action<int>? FocusReleased;

    public bool IsConnected { get; private set; }

    /// <summary>True while a connection attempt (including retries) is in progress.</summary>
    public bool IsConnecting => _connecting;

    public bool IsFullScreen { get; private set; }

    /// <summary>Current connection state, read live from the COM object.
    /// The Connected property is a VARIANT_BOOL (Int16): -1 = true, 0 = false.
    /// </summary>
    public bool GetConnectedState()
    {
        lock (_ocxGate)
        {
            var ocx = GetOcx();
            if (ocx == null) return false;
            try
            {
                var value = GetComProperty(ocx, "Connected");
                return ConvertToBool(value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Connected state");
                return false;
            }
        }
    }

    /// <summary>Converts a COM VARIANT_BOOL (Int16) — and related primitive types — to bool.</summary>
    private static bool ConvertToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        IConvertible c => c.ToInt32(null) != 0,
        _ => false,
    };

    /// <summary>
    /// Connects to a remote/child session at 127.0.0.1 with the given desktop size.
    /// Must run on the UI thread (the ActiveX control is STA).
    ///
    /// <paramref name="useChildSession"/>: if true, sets ConnectToChildSession=true
    /// (Windows child session — only works on systems that support it).
    /// If false (default), performs a standard RDP connection using the provided
    /// credentials — the widely-compatible path that works with TermWrap-enabled
    /// multi-session on Windows Home.
    ///
    /// The first connection attempt creates the target session itself, which can
    /// take a moment — so we retry with exponential backoff (1s/2s/4s).
    /// </summary>
    public void Connect(
        int desktopWidth, int desktopHeight, int colorDepth, int rdpPort,
        bool smartSizing, bool keyboardHookToRemote, bool audioRedirected,
        string? userName = null, string? password = null, bool useChildSession = false)
    {
        if (_connecting || GetConnectedState())
        {
            _logger.LogWarning("Connect ignored: already connecting/connected");
            return;
        }

        _connecting = true;
        IsFullScreen = false;

        // Reset the cancellation token for this connect attempt. DisconnectSession
        // (or Dispose) will cancel it so the retry loop exits cleanly when the
        // user clicks "Disconnect" mid-retry.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _connectCts, cts)?.Dispose();

        // Run the connection loop on the UI thread (ActiveX is STA).
        // BeginInvoke keeps the caller non-blocking; retry delays happen on a
        // worker thread but each attempt marshals back to the UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                const int maxRetries = 3;
                for (var attempt = 1; attempt <= maxRetries; attempt++)
                {
                    if (cts.IsCancellationRequested || IsDisposed || Disposing)
                    {
                        _connecting = false;
                        return;
                    }

                    try
                    {
                        // Marshal the connect attempt to the UI thread
                        var ok = await InvokeOnUiThreadAsync(() =>
                            TryConnectCore(
                                desktopWidth, desktopHeight, colorDepth, rdpPort,
                                smartSizing, keyboardHookToRemote, audioRedirected,
                                userName, password, useChildSession));

                        if (ok) return;

                        _logger.LogWarning("Session connect attempt {Attempt}/{Max} failed, retrying", attempt, maxRetries);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Session connect attempt {Attempt}/{Max} threw, retrying", attempt, maxRetries);
                    }

                    if (attempt < maxRetries)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            _connecting = false;
                            return;
                        }
                    }
                }
            }
            finally
            {
                _connecting = false;
            }
        });
    }

    /// <summary>Back-compat: connects to a Windows child session (where supported).</summary>
    public void ConnectToChildSession(
        int desktopWidth, int desktopHeight, int colorDepth, int rdpPort,
        bool smartSizing, bool keyboardHookToRemote, bool audioRedirected)
    {
        Connect(desktopWidth, desktopHeight, colorDepth, rdpPort,
            smartSizing, keyboardHookToRemote, audioRedirected,
            userName: null, password: null, useChildSession: true);
    }

    /// <summary>Marshals a function to the UI (STA) thread and awaits its result.</summary>
    private Task<bool> InvokeOnUiThreadAsync(Func<bool> action)
    {
        if (InvokeRequired)
        {
            var tcs = new TaskCompletionSource<bool>();
            BeginInvoke(() =>
            {
                try { tcs.TrySetResult(action()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }
        try
        {
            return Task.FromResult(action());
        }
        catch (Exception ex)
        {
            return Task.FromException<bool>(ex);
        }
    }

    /// <summary>Attempts one connection. Returns true if the connect call was made.</summary>
    private bool TryConnectCore(
        int desktopWidth, int desktopHeight, int colorDepth, int rdpPort,
        bool smartSizing, bool keyboardHookToRemote, bool audioRedirected,
        string? userName, string? password, bool useChildSession)
    {
        try
        {
            // Force the ActiveX to fully initialize (BetterGI's GetRequiredOcx
            // pattern): reading Handle creates the underlying window + control
            // even when the host is not yet visible.
            _ = Handle;

            var client = GetOcx();
            if (client == null) throw new InvalidOperationException("RDP ActiveX OCX not available");

            // --- Core connection ---
            SetComProperty(client, "Server", "localhost");
            SetComProperty(client, "DesktopWidth", desktopWidth);
            SetComProperty(client, "DesktopHeight", desktopHeight);
            SetComProperty(client, "ColorDepth", colorDepth);
            SetComProperty(client, "ConnectingText", "正在创建 AkiSpace 桌面分身...");
            SetComProperty(client, "DisconnectedText", "AkiSpace 桌面分身已断开");

            // --- Secured settings (via SecuredSettings2) ---
            var secured = GetComProperty(client, "SecuredSettings2");
            if (secured != null)
            {
                SetComProperty(secured, "KeyboardHookMode", keyboardHookToRemote ? 1 : 0);
                SetComProperty(secured, "AudioRedirectionMode", audioRedirected ? 0 : 2);
            }

            // --- Advanced settings (via AdvancedSettings7 / AdvancedSettings9) ---
            var adv = GetComProperty(client, "AdvancedSettings7");
            if (adv != null)
            {
                SetComProperty(adv, "RDPPort", rdpPort);
                SetComProperty(adv, "EnableCredSspSupport", true);
                SetComProperty(adv, "EnableWindowsKey", 1);
                SetComProperty(adv, "SmartSizing", smartSizing);
            }

            // --- Credentials (standard RDP path only) ---
            // ChildSession mode: BetterGI does NOT set UserName/Password — the OS handles it.
            if (!useChildSession)
            {
                var adv9 = GetComProperty(client, "AdvancedSettings9");
                if (adv9 != null)
                {
                    // 2 = AttemptAuthentication: log/warn on localhost cert
                    // mismatch instead of silently skipping (level 0) which
                    // left the user open to NLA credential theft if a
                    // malicious listener bound 3389 first.
                    SetComProperty(adv9, "AuthenticationLevel", 2);
                    SetComProperty(adv9, "RedirectClipboard", false);
                    SetComProperty(adv9, "RedirectDrives", false);
                    SetComProperty(adv9, "RedirectDevices", false);
                    SetComProperty(adv9, "RedirectPrinters", false);
                    SetComProperty(adv9, "RedirectSmartCards", false);

                    // Performance: disable wallpaper, menu animations, theming; enable font smoothing
                    const int tsPerfDisableWallpaper = 0x01;
                    const int tsPerfDisableFullWindowDrag = 0x02;
                    const int tsPerfDisableMenuAnimations = 0x04;
                    const int tsPerfDisableTheming = 0x08;
                    const int tsPerfEnableFontSmoothing = 0x80;
                    SetComProperty(adv9, "PerformanceFlags",
                        tsPerfDisableWallpaper | tsPerfDisableFullWindowDrag |
                        tsPerfDisableMenuAnimations | tsPerfDisableTheming | tsPerfEnableFontSmoothing);

                    var adv8 = GetComProperty(client, "AdvancedSettings8");
                    if (adv8 != null)
                        SetComProperty(adv8, "NetworkConnectionType", 6); // LAN
                }

                if (!string.IsNullOrEmpty(userName))
                {
                    SetComProperty(client, "UserName", userName);
                    // Log at Debug (not Info) — userName can be a personal
                    // identifier and Info is enabled by default.
                    _logger.LogDebug("Set UserName (length={Length})", userName.Length);
                }
                if (!string.IsNullOrEmpty(password))
                {
                    // Password goes through IMsRdpClientNonScriptable (via IUnknown QI,
                    // same technique as GetExtendedSettings)
                    var nonScriptable = GetNonScriptable(client);
                    nonScriptable.put_ClearTextPassword(password);
                    _logger.LogInformation("Set ClearTextPassword (masked)");
                }
            }

            // --- Child-session mode (BetterGI pattern: only ConnectToChildSession + EnableZoom) ---
            if (useChildSession)
            {
                // Reset diagnostic fields for this attempt.
                _lastElevated = false;
                _lastUser = null;
                _lastConnectToChildSessionReadback = null;
                _lastWtsEnableOk = false;
                _lastWtsEnableErr = 0;
                _lastWtsIsEnabled = false;
                _lastWtsIsEnabledErr = 0;

                // CRITICAL: prime the WTS child-session broker in the current process
                // context, matching BetterGI's EnsureChildSessionsEnabled behaviour.
                // Even though env checks show WTSIsChildSessionsEnabled=True at the
                // WTS level, the broker can still reject requests from processes that
                // never re-enabled the feature in their own session context.
                try
                {
                    if (WtsApi.WTSEnableChildSessions(true))
                    {
                        _lastWtsEnableOk = true;
                    }
                    else
                    {
                        _lastWtsEnableErr = Marshal.GetLastWin32Error();
                        _logger.LogWarning(
                            "WTSEnableChildSessions(true) returned false, Win32 error {Error}",
                            _lastWtsEnableErr);
                    }
                }
                catch (Exception ex)
                {
                    _lastWtsEnableErr = -1;
                    _logger.LogWarning(ex, "WTSEnableChildSessions(true) threw");
                }
                try
                {
                    bool isEnabled = false;
                    if (WtsApi.WTSIsChildSessionsEnabled(out isEnabled))
                    {
                        _lastWtsIsEnabled = isEnabled;
                    }
                    else
                    {
                        _lastWtsIsEnabledErr = Marshal.GetLastWin32Error();
                    }
                }
                catch (Exception ex)
                {
                    _lastWtsIsEnabledErr = -1;
                    _logger.LogDebug(ex, "WTSIsChildSessionsEnabled threw");
                }

                // Diagnostic: process integrity / elevation. ConnectToChildSession
                // requires a non-elevated (medium-IL) token of the current user.
                // If the app is launched elevated (e.g. "Run as administrator" or from
                // an elevated shell), the child-session broker rejects the request
                // and the control falls back to a normal TCP connect — which then
                // fails with "remote computer cannot be reached" (disconnect 516).
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    var principal = new WindowsPrincipal(identity);
                    _lastElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
                    _lastUser = identity.Name;
                    _logger.LogInformation(
                        "Child-session connect: user={User} elevated={Elevated} session={Session}",
                        identity.Name, _lastElevated, identity.User?.Value);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to query process identity");
                }

                // BetterGI casts client directly to IMsRdpExtendedSettings — no QI.
                var extended = (RdpCom.IMsRdpExtendedSettings)client;

                // EnableZoom is optional — wrap in try/catch like BetterGI's
                // TrySetExtendedProperty. If it throws, we must still set
                // ConnectToChildSession (otherwise the connection silently
                // falls back to a normal RDP connection without credentials).
                try
                {
                    object enableZoom = true;
                    extended.set_Property("EnableZoom", ref enableZoom);
                }
                catch (COMException)
                {
                    // Older MsTscAx versions may not support EnableZoom; continue.
                    _logger.LogDebug("EnableZoom not supported on this control version, continuing");
                }

                object connectToChildSession = true;
                extended.set_Property("ConnectToChildSession", ref connectToChildSession);
                _logger.LogInformation("Set ConnectToChildSession extended property");

                // Diagnostic: read back to confirm the control accepted the
                // extended property. If the value isn't true, the control
                // silently fell back to a standard TCP RDP connection.
                try
                {
                    object readback = extended.get_Property("ConnectToChildSession");
                    if (readback is bool b) _lastConnectToChildSessionReadback = b;
                    else if (readback is short s) _lastConnectToChildSessionReadback = s != 0;
                    _logger.LogInformation(
                        "ConnectToChildSession readback = {Value} ({Type})",
                        readback, readback?.GetType().FullName ?? "null");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read back ConnectToChildSession");
                }
            }

            _logger.LogInformation("Connecting to {Mode} via localhost:{Port} ({W}x{H})",
                useChildSession ? "child session" : "RDP session",
                rdpPort, desktopWidth, desktopHeight);
            InvokeComMethod(client, "Connect");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TryConnectCore failed");
            ConnectionFailed?.Invoke($"连接失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>Retrieves IMsRdpClientNonScriptable from the OCX via IUnknown QI.</summary>
    private static RdpCom.IMsRdpClientNonScriptable GetNonScriptable(object ocx)
    {
        var pUnknown = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(ocx);
        try
        {
            var iid = new Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE");
            var hr = System.Runtime.InteropServices.Marshal.QueryInterface(pUnknown, ref iid, out var pInterface);
            if (hr != 0)
            {
                throw new System.Runtime.InteropServices.COMException(
                    $"IMsRdpClientNonScriptable not available (HRESULT 0x{hr:X8})", hr);
            }
            return (RdpCom.IMsRdpClientNonScriptable)
                System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(pInterface);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.Release(pUnknown);
        }
    }

    /// <summary>Disconnects the RDP session (child session stays alive).</summary>
    public void DisconnectSession()
    {
        // Cancel any in-flight retry loop BEFORE clearing _connecting, so the
        // background Task.Run can observe the cancel and exit cleanly without
        // firing a second connect attempt.
        Interlocked.Exchange(ref _connectCts, null)?.Cancel();
        _connecting = false;
        IsConnected = false;
        try
        {
            var client = GetOcx();
            if (client != null && GetConnectedState())
            {
                InvokeComMethod(client, "Disconnect");
                _logger.LogInformation("RDP session disconnected");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disconnect failed");
        }
    }

    /// <summary>
    /// Finds the RDP control's hidden "Input Capture Window" child and focuses it,
    /// directing keyboard input to the remote session. Returns true on success.
    /// </summary>
    public bool TryFocusRdpInputWindow()
    {
        var handle = FindInputCaptureWindow();
        if (handle == IntPtr.Zero) return false;
        User32.SetFocus(handle);
        return User32.GetFocus() == handle;
    }

    /// <summary>
    /// Whether the RDP input capture window currently has keyboard focus.
    /// Uses the thread-safe GetGUIThreadInfo query (via GetFocusedWindowHandle)
    /// because this may be called from the MouseForwarder polling timer on a
    /// thread-pool thread, where thread-local GetFocus() would always return Zero.
    /// </summary>
    public bool IsRdpInputWindowFocused()
    {
        var handle = FindInputCaptureWindow();
        if (handle == IntPtr.Zero) return false;
        return User32.GetFocusedWindowHandle() == handle;
    }

    /// <summary>Clears the cached input-window handle (call after reconnect).</summary>
    public void ClearInputWindowCache()
    {
        lock (_ocxGate) _inputWindow = null;
    }

    /// <summary>
    /// Sends scan-code key strokes to the remote session via
    /// IMsRdpClientNonScriptable.SendKeys. strokes = (scanCode, extended, keyUp) triples.
    /// </summary>
    public void SendKeys(params (int ScanCode, bool Extended, bool KeyUp)[] strokes)
    {
        if (strokes is null || strokes.Length == 0) return;
        lock (_ocxGate)
        {
            var ocx = GetOcx();
            if (ocx is not RdpCom.IMsRdpClientNonScriptable nonScriptable)
            {
                _logger.LogWarning("SendKeys skipped: IMsRdpClientNonScriptable unavailable");
                return;
            }

            var keyUpStates = new short[strokes.Length];
            var keyData = new int[strokes.Length];
            for (var i = 0; i < strokes.Length; i++)
            {
                keyUpStates[i] = strokes[i].KeyUp ? (short)-1 : (short)0;
                keyData[i] = strokes[i].ScanCode | (strokes[i].Extended ? InputConstants.ScanCodeExtendedFlag : 0);
            }

            nonScriptable.SendKeys(strokes.Length, ref keyUpStates[0], ref keyData[0]);
        }
    }

    // ---------------------------------------------------------------- internals

    private IntPtr FindInputCaptureWindow()
    {
        lock (_ocxGate)
        {
            if (_inputWindow is IntPtr cached && cached != IntPtr.Zero && User32.IsWindow(cached))
                return cached;

            var hostHandle = Handle;
            IntPtr found = IntPtr.Zero;
            User32.EnumChildWindows(hostHandle, (hWnd, _) =>
            {
                var sb = new System.Text.StringBuilder(256);
                User32.GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString() == "Input Capture Window")
                {
                    found = hWnd;
                    return false; // stop enumerating
                }
                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero)
                _inputWindow = found;
            else
                _logger.LogDebug("Input Capture Window not found yet (session may not be fully up)");

            return found;
        }
    }

    // ---- COM reflection helpers (BetterGI's proven pattern) ----

    private int TryGetExtendedDisconnectReason()
    {
        lock (_ocxGate)
        {
            try
            {
                var ocx = GetOcx();
                if (ocx == null) return 0;
                return Convert.ToInt32(GetComProperty(ocx, "ExtendedDisconnectReason"));
            }
            catch
            {
                return 0;
            }
        }
    }

    private string? TryGetErrorDescription(int disconnectReason, int extendedDisconnectReason)
    {
        lock (_ocxGate)
        {
            try
            {
                var ocx = GetOcx();
                if (ocx == null) return null;
                return Convert.ToString(
                    InvokeComMethod(ocx, "GetErrorDescription", disconnectReason, extendedDisconnectReason),
                    System.Globalization.CultureInfo.CurrentCulture);
            }
            catch
            {
                return null;
            }
        }
    }

    private static object? GetComProperty(object target, string propertyName) =>
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            null, target, null);

    private static void SetComProperty(object target, string propertyName, object value) =>
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            null, target, new[] { value });

    private static object? InvokeComMethod(object target, string methodName, params object[] args) =>
        target.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod,
            null, target, args);

    // ---- AxHost event wiring ----

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        try
        {
            var ocx = GetOcx();
            if (ocx != null)
            {
                _logger.LogInformation("RDP ActiveX control created: {Type}", ocx.GetType().FullName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inspect RDP ActiveX OCX");
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Focus fix: when the user clicks the RDP area, focus the input capture window
        if (m.Msg == InputConstants.WM_MOUSEACTIVATE)
        {
            TryFocusRdpInputWindow();
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Dispatches the IMsTscAxEvents sink callbacks. The AxHost raises these via
    /// the event sink the AxHost infrastructure wires automatically; we surface
    /// the events we care about through this method (called from the event sink).
    /// </summary>
    internal void RaiseEvent(int dispId, params object?[] args)
    {
        switch (dispId)
        {
            case 3: // OnLoginComplete
                {
                    _connecting = false;
                    IsConnected = true;
                    _logger.LogInformation("RDP login complete — desktop ready");
                    LoginCompleted?.Invoke();
                    break;
                }
            case 4: // OnDisconnected
                {
                    _connecting = false;
                    IsConnected = false;
                    var reason = args.Length > 0 ? Convert.ToInt32(args[0]) : -1;
                    var extended = TryGetExtendedDisconnectReason();
                    var description = TryGetErrorDescription(reason, extended);
                    _logger.LogWarning("RDP disconnected, reason {Reason}, extended {Extended}, desc: {Desc}",
                        reason, extended, description);
                    // Surface the raw reason/extended codes alongside the description and
                    // the last child-session diagnostic snapshot — the user sees the real
                    // failure without needing DebugView. For child-session mode this
                    // distinguishes between (a) process elevated → broker rejected,
                    // (b) ConnectToChildSession not accepted by the control → fell back
                    // to a plain TCP RDP connection that failed, and (c) genuine
                    // transport / NLA / logon failures.
                    var diagnosticLines = new List<string>();
                    if (_lastUser is not null)
                    {
                        diagnosticLines.Add($"  进程用户: {_lastUser}");
                        diagnosticLines.Add($"  提升 (管理员): {(_lastElevated ? "是 ⚠ 子会话需要非提升 token" : "否 ✓")}");
                    }
                    if (_lastConnectToChildSessionReadback is bool rb)
                    {
                        diagnosticLines.Add($"  ConnectToChildSession 读回: {(rb ? "True ✓" : "False ⚠ 控件未接受该属性")}");
                    }
                    // WTS child-session broker state captured right before connect.
                    diagnosticLines.Add(_lastWtsEnableOk
                        ? "  WTSEnableChildSessions(true): True ✓ broker 已激活"
                        : $"  WTSEnableChildSessions(true): False ⚠ Win32 错误 {_lastWtsEnableErr}（broker 未激活）");
                    diagnosticLines.Add(_lastWtsIsEnabledErr == 0
                        ? $"  WTSIsChildSessionsEnabled: {_lastWtsIsEnabled}{(_lastWtsIsEnabled ? " ✓" : " ⚠")}"
                        : $"  WTSIsChildSessionsEnabled: 失败 ⚠ Win32 错误 {_lastWtsIsEnabledErr}");
                    // If this disconnect was from a child-session attempt, also query
                    // whether the broker actually created a child session. This
                    // distinguishes "broker rejected the request" (NoChildSessionId or
                    // WTSAPI failure) from "broker created the session but the RDP
                    // handshake then failed". Show the result unconditionally so a
                    // false return from the P/Invoke is itself visible.
                    try
                    {
                        bool got;
                        uint childSessionId;
                        try
                        {
                            got = WtsApi.WTSGetChildSessionId(out childSessionId);
                        }
                        catch (DllNotFoundException dllEx)
                        {
                            diagnosticLines.Add($"  WTSGetChildSessionId: DllNotFoundException ⚠ {dllEx.Message}");
                            got = false;
                            childSessionId = 0;
                        }
                        if (got)
                        {
                            diagnosticLines.Add(
                                childSessionId == WtsApi.NoChildSessionId
                                    ? "  WTSGetChildSessionId: 无 ⚠ broker 未创建子会话（请求被拒或子会话被关闭）"
                                    : $"  WTSGetChildSessionId: {childSessionId} ✓ broker 已创建子会话");
                        }
                        else
                        {
                            var win32err = Marshal.GetLastWin32Error();
                            diagnosticLines.Add($"  WTSGetChildSessionId: 返回 false ⚠ Win32 错误 {win32err}（broker 调用失败）");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to query child session id");
                    }
                    var surfaced = description is null
                        ? $"连接已断开（原因代码 {reason}，扩展代码 {extended}）"
                        : $"{description}（原因代码 {reason}，扩展代码 {extended}）";
                    if (diagnosticLines.Count > 0)
                    {
                        surfaced += "\n诊断信息：\n" + string.Join("\n", diagnosticLines);
                    }
                    ConnectionFailed?.Invoke(surfaced);
                    break;
                }
            case 10: // OnFatalError (correct DispId = 10, was wrongly 6)
                {
                    _connecting = false;
                    IsConnected = false;
                    var code = args.Length > 0 ? Convert.ToInt32(args[0]) : -1;
                    _logger.LogError("RDP fatal error {Code}", code);
                    ConnectionFailed?.Invoke($"RDP 致命错误（代码 {code}）");
                    break;
                }
            case 8: // OnRequestGoFullScreen (correct DispId = 8, was wrongly 13)
                IsFullScreen = true;
                RequestedGoFullScreen?.Invoke();
                break;
            case 9: // OnRequestLeaveFullScreen (correct DispId = 9, was wrongly 14)
                IsFullScreen = false;
                RequestedLeaveFullScreen?.Invoke();
                break;
            case 23: // OnFocusReleased (correct DispId = 23, was wrongly 15)
                {
                    var direction = args.Length > 0 ? Convert.ToInt32(args[0]) : 0;
                    FocusReleased?.Invoke(direction);
                    break;
                }
            case 22: // OnLogonError (correct DispId = 22, was wrongly 23)
                {
                    _connecting = false;
                    IsConnected = false;
                    var code = args.Length > 0 ? Convert.ToInt32(args[0]) : -1;
                    _logger.LogError("RDP logon error {Code}", code);

                    // Non-terminal logon events (per BetterGI's IsNonTerminalLogonEvent):
                    // -5, -4, -2, 3 are intermediate logon events where the user is prompted
                    // but the connection can still succeed. Only terminal logon errors should
                    // show an error dialog and set IsConnected = false.
                    if (code is -5 or -4 or -2 or 3)
                    {
                        _logger.LogInformation("RDP non-terminal logon event {Code}, continuing", code);
                        // Don't show error dialog, don't mark as disconnected, connection continues
                    }
                    else
                    {
                        _logger.LogError("RDP logon error {Code}", code);
                        ConnectionFailed?.Invoke($"登录失败（错误代码 {code}）");
                    }
                    break;
                }
            case 33: // OnAutoReconnected (treat as successful login)
                {
                    _connecting = false;
                    IsConnected = true;
                    _logger.LogInformation("RDP auto-reconnected — desktop ready");
                    LoginCompleted?.Invoke();
                    break;
                }
        }
    }
}

/// <summary>
/// COM event sink that forwards IMsTscAxEvents to the RdpActiveXHost.
/// Must be [ComVisible] so the connection point can marshal to it.
///
/// Three out-parameter events are critical for the child session handshake:
///   OnReceivedTSPublicKey → continueLogon MUST be set to true (or the logon
///                           is aborted and the connection fails immediately).
///   OnConfirmClose        → allowClose MUST be set to true (or the window
///                           cannot be closed by the user).
///   OnAutoReconnecting    → continueStatus MUST be set to Automatic (0) so
///                           that reconnection attempts proceed.
/// </summary>
[ComVisible(true)]
internal sealed class RdpEventSink : RdpCom.IMsTscAxEvents
{
    private readonly RdpActiveXHost _owner;
    public RdpEventSink(RdpActiveXHost owner) => _owner = owner;

    // ---- connection lifecycle ----

    public void OnConnecting() => _owner.RaiseEvent(1);
    public void OnConnected() => _owner.RaiseEvent(2);
    public void OnLoginComplete() => _owner.RaiseEvent(3);
    public void OnDisconnected([System.Runtime.InteropServices.In] int disconnectReason)
        => _owner.RaiseEvent(4, disconnectReason);

    // ---- fullscreen / layout ----

    public void OnEnterFullScreenMode() { }
    public void OnLeaveFullScreenMode() { }
    public void OnRequestGoFullScreen() => _owner.RaiseEvent(8);
    public void OnRequestLeaveFullScreen() => _owner.RaiseEvent(9);
    public void OnRemoteDesktopSizeChange([System.Runtime.InteropServices.In] int width, [System.Runtime.InteropServices.In] int height) { }

    // ---- channel data ----

    public void OnChannelReceivedData(
        [System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string channelName,
        [System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string data)
    { }

    // ---- error / warning ----

    public void OnFatalError([System.Runtime.InteropServices.In] int errorCode) => _owner.RaiseEvent(10, errorCode);
    public void OnWarning([System.Runtime.InteropServices.In] int warningCode) { }

    // ---- idle / container ----

    public void OnIdleTimeoutNotification() { }
    public void OnRequestContainerMinimize() { }

    // ---- CRITICAL out-parameter events (child session handshake) ----

    public void OnConfirmClose([System.Runtime.InteropServices.Out] out bool allowClose)
    {
        allowClose = true;
    }

    public void OnReceivedTSPublicKey(
        [System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string publicKey,
        [System.Runtime.InteropServices.Out] out bool continueLogon)
    {
        // MUST be true — otherwise the ActiveX aborts the connection handshake
        // and shows the generic "cannot connect to remote computer" dialog.
        continueLogon = true;
    }

    public void OnAutoReconnecting(
        [System.Runtime.InteropServices.In] int disconnectReason,
        [System.Runtime.InteropServices.In] int attemptCount,
        [System.Runtime.InteropServices.Out] out RdpCom.AutoReconnectContinueState continueStatus)
    {
        continueStatus = RdpCom.AutoReconnectContinueState.Automatic;
    }

    // ---- authentication warnings ----

    public void OnAuthenticationWarningDisplayed() { }
    public void OnAuthenticationWarningDismissed() { }

    // ---- remote programs (not used in child session full-desktop mode) ----

    public void OnRemoteProgramResult(
        [System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string remoteProgram,
        [System.Runtime.InteropServices.In] RdpCom.RemoteProgramResult error,
        [System.Runtime.InteropServices.In] bool isExecutable)
    { }

    public void OnRemoteProgramDisplayed(
        [System.Runtime.InteropServices.In] bool displayed,
        [System.Runtime.InteropServices.In] uint displayInformation)
    { }

    public void OnRemoteWindowDisplayed(
        [System.Runtime.InteropServices.In] bool displayed,
        [System.Runtime.InteropServices.In] ref RdpCom.RemotableHandle windowHandle,
        [System.Runtime.InteropServices.In] RdpCom.RemoteWindowDisplayedAttribute windowAttribute)
    { }

    // ---- logon ----

    public void OnLogonError([System.Runtime.InteropServices.In] int errorCode) => _owner.RaiseEvent(22, errorCode);
    public void OnUserNameAcquired(
        [System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string userName)
    { }

    // ---- focus / input ----

    public void OnFocusReleased([System.Runtime.InteropServices.In] int direction) => _owner.RaiseEvent(23, direction);
    public void OnMouseInputModeChanged([System.Runtime.InteropServices.In] bool isRelativeMouseMode) { }

    // ---- misc ----

    public void OnServiceMessageReceived(
        [System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] string serviceMessage)
    { }

    public void OnConnectionBarPullDown() { }
    public void OnNetworkStatusChanged(
        [System.Runtime.InteropServices.In] uint qualityLevel,
        [System.Runtime.InteropServices.In] int bandwidth,
        [System.Runtime.InteropServices.In] int roundTripTime)
    { }

    public void OnDevicesButtonPressed() { }

    public void OnAutoReconnected()
    {
        // After auto-reconnect, treat as a successful login (same as BetterGI).
        _owner.RaiseEvent(33);
    }

    public void OnAutoReconnecting2(
        [System.Runtime.InteropServices.In] int disconnectReason,
        [System.Runtime.InteropServices.In] bool networkAvailable,
        [System.Runtime.InteropServices.In] int attemptCount,
        [System.Runtime.InteropServices.In] int maxAttemptCount)
    { }
}