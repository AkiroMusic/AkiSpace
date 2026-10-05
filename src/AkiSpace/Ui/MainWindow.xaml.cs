using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Forms.Integration;
using AkiSpace.App;
using AkiSpace.Common;
using AkiSpace.Controls;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Ui;

/// <summary>
/// WPF main window: renders <see cref="ConnectionController"/> state and hosts the
/// RdpActiveXHost (AxHost) inside a WindowsFormsHost. Orchestration stays in the
/// App layer; the window only translates state to controls and window behaviour
/// (fullscreen requests, dark titlebar, session-end handling).
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConnectionController _controller;

    private WindowsFormsHost? _formsHost;
    private bool _sessionEnding;

    public MainWindow(ConnectionController controller)
    {
        InitializeComponent();
        _controller = controller;
        BindController();

        TxtVersion.Text = typeof(MainWindow).Assembly.GetName().Version is { } version
            ? $"v{version.Major}.{version.Minor}.{version.Build}"
            : string.Empty;

        SourceInitialized += (_, _) =>
        {
            ApplyDarkTitleBar();
            var source = (HwndSource)PresentationSource.FromVisual(this)!;
            source.AddHook(WndProcHook);
        };

        UpdateLanguageButton();
        Loc.LanguageChanged += OnLanguageChangedUi;
    }

    // ---------------------------------------------------------------- language toggle

    /// <summary>Toolbar top-right toggle: applies the other language AND persists it
    /// immediately, so the choice survives restart without opening Settings.</summary>
    private void OnToggleLanguage(object sender, RoutedEventArgs e)
    {
        Loc.SetLanguage(Loc.Language == Loc.Chinese ? Loc.English : Loc.Chinese, persist: true);
    }

    /// <summary>The toggle shows the language you would switch TO, with a matching tooltip.</summary>
    private void OnLanguageChangedUi() => Dispatcher.Invoke(UpdateLanguageButton);

    private void UpdateLanguageButton()
    {
        var toZh = Loc.Language != Loc.Chinese;
        LanguageText.Text = toZh ? "中文" : "English";
        BtnLanguage.ToolTip = toZh ? "切换到中文界面" : "Switch to English";
        System.Windows.Automation.AutomationProperties.SetName(BtnLanguage, toZh ? "切换到中文" : "Switch to English");
    }

    private void BindController()
    {
        _controller.ConnectStateChanged += ApplyConnectState;
        _controller.ChildSessionStatusChanged += OnChildSessionStatus;
        _controller.WrapperStatusChanged += OnWrapperStatus;
        _controller.PerformanceStatusChanged += OnPerformanceStatus;
        _controller.RdpHostChanged += OnRdpHostChanged;
        _controller.FullScreenRequested += OnFullScreenRequested;
        _controller.LeaveFullScreenRequested += OnLeaveFullScreenRequested;
    }

    private void UnbindController()
    {
        _controller.ConnectStateChanged -= ApplyConnectState;
        _controller.ChildSessionStatusChanged -= OnChildSessionStatus;
        _controller.WrapperStatusChanged -= OnWrapperStatus;
        _controller.PerformanceStatusChanged -= OnPerformanceStatus;
        _controller.RdpHostChanged -= OnRdpHostChanged;
        _controller.FullScreenRequested -= OnFullScreenRequested;
        _controller.LeaveFullScreenRequested -= OnLeaveFullScreenRequested;
    }

    private void OnChildSessionStatus(StatusLine line) => Dispatcher.Invoke(() => SetStatus(LblChildSession, DotChildSession, line));
    private void OnWrapperStatus(StatusLine line) => Dispatcher.Invoke(() => SetStatus(LblWrapper, DotWrapper, line));
    private void OnPerformanceStatus(string text) => Dispatcher.Invoke(() => LblPerformance.Text = text);
    private void OnFullScreenRequested() => Dispatcher.Invoke(EnterFullScreen);
    private void OnLeaveFullScreenRequested() => Dispatcher.Invoke(LeaveFullScreen);

    private void ApplyConnectState(ConnectUiState s)
    {
        BtnConnect.IsEnabled = s.ConnectEnabled;
        BtnDisconnect.IsEnabled = s.DisconnectEnabled;
        BtnTerminate.IsEnabled = s.TerminateEnabled;
        BtnGameMouse.Visibility = s.GameMouseVisible ? Visibility.Visible : Visibility.Collapsed;
        BtnGameMouse.IsEnabled = s.GameMouseEnabled;
        GameMouseText.Text = s.GameMouseText;
        BtnLaunch.IsEnabled = s.LaunchEnabled;
        SetStatus(LblConnection, DotConnection, new StatusLine(s.ConnectionStatusText, s.ConnectionLevel));
        EnsureWindowEnabled();
    }

    /// <summary>
    /// The MSTSC ActiveX disables its owner chain while connecting/connected — observed
    /// as the whole main window becoming WS_DISABLED after a child-session login, which
    /// makes the title-bar X (and CloseMainWindow) silently dead. RefreshStatus calls
    /// this every second; re-enable when something disabled us. Skipped while any other
    /// window of this thread is open (modal dialogs, MessageBoxes, popups) — blindly
    /// re-enabling would let clicks through behind a "modal" dialog.
    /// </summary>
    private void EnsureWindowEnabled()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || User32.IsWindowEnabled(hwnd)) return;
            if (HasOpenThreadWindow(hwnd)) return;
            User32.EnableWindow(hwnd, true);
            AppShellServices.LoggerFor<MainWindow>()
                .LogWarning("Main window was disabled (ActiveX owner-disable); re-enabled");
        }
        catch (Exception ex)
        {
            AppShellServices.LoggerFor<MainWindow>().LogDebug(ex, "EnsureWindowEnabled failed");
        }
    }

    /// <summary>True when any visible window of THIS UI thread other than
    /// <paramref name="mainHwnd"/> exists (WPF ShowDialog, native MessageBox, popups).</summary>
    private bool HasOpenThreadWindow(IntPtr mainHwnd)
    {
        var mainThreadId = User32.GetWindowThreadProcessId(mainHwnd, out _);
        var found = false;
        User32.EnumWindows((h, _) =>
        {
            if (h == mainHwnd || !User32.IsWindowVisible(h)) return true;
            uint pid;
            if (User32.GetWindowThreadProcessId(h, out pid) == mainThreadId)
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void OnRdpHostChanged(RdpActiveXHost? host)
    {
        if (_formsHost is not null)
        {
            _formsHost.Child = null;
            ViewerArea.Children.Remove(_formsHost);
            _formsHost = null;
        }
        BtnScreenshot.IsEnabled = host is not null; // capture needs a live clone session
        EmptyState.Visibility = host is null ? Visibility.Visible : Visibility.Collapsed;
        if (host is null) return;

        // WindowsFormsHost.EnableWindowsFormsInterop() has already run in the shell.
        _formsHost = new WindowsFormsHost { Child = host };
        ViewerArea.Children.Add(_formsHost);
    }

    private void EnterFullScreen()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
    }

    private void LeaveFullScreen()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = WindowState.Normal;
    }

    // ---------------------------------------------------------------- button handlers

    private async void OnConnect(object sender, RoutedEventArgs e) => await _controller.ConnectAsync();
    private void OnDisconnect(object sender, RoutedEventArgs e) => _controller.Disconnect();
    private void OnTerminate(object sender, RoutedEventArgs e) => _controller.TerminateChildSession();
    private void OnGameMouse(object sender, RoutedEventArgs e) => _controller.ToggleGameMouse();
    private void OnLaunch(object sender, RoutedEventArgs e) => _controller.LaunchProgramInChildSession();
    private void OnScreenshot(object sender, RoutedEventArgs e) => CaptureChildScreenToClipboard();
    private void OnSetup(object sender, RoutedEventArgs e)
    {
        var setup = new SetupWindow(
            AppShellServices.LoggerFor<SetupWindow>(),
            AppShellServices.EnvironmentVerifier,
            AppShellServices.ChildSessionManager,
            AppShellServices.Settings);
        setup.Owner = this;
        setup.ShowDialog();
        _controller.RefreshStatus();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(AppShellServices.Settings);
        settings.Owner = this;
        var saved = settings.ShowDialog() == true;
        if (saved) _controller.SetStatusText("Conn_SettingsSaved");
    }

    private void OnGitHubLink(object sender, RoutedEventArgs e) =>
        OpenRepository();

    internal static void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/AkiroMusic/AkiSpace") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppShellServices.LoggerFor<MainWindow>().LogWarning(ex, "Failed to open repository link");
        }
    }

    // ---------------------------------------------------------------- clone screenshot

    /// <summary>
    /// One-click clone-screen capture to the HOST clipboard (toolbar button, tray
    /// item or Ctrl+Shift+S). View-side only: PrintWindow renders the RDP host
    /// surface; a failure reports status and never touches the session.
    /// </summary>
    public void CaptureChildScreenToClipboard()
    {
        var host = _formsHost?.Child as RdpActiveXHost;
        var hwnd = host is { IsHandleCreated: true } ? host.Handle : IntPtr.Zero;
        if (hwnd == IntPtr.Zero
            || !ChildScreenCapture.TryCapture(hwnd, out var bitmap)
            || bitmap is null
            || !ChildScreenCapture.CopyToClipboard(bitmap))
        {
            _controller.SetStatusText("Shot_StatusFail");
            ShowToast(Loc.T("Shot_ToastFail"), success: false);
            return;
        }
        _controller.SetStatusText("Shot_StatusOk", StatusLevel.Good);
        ShowToast(Loc.T("Shot_ToastOk"));
    }

    /// <summary>Sets a status label and its lead dot in one call (dot color follows the level).</summary>
    private static void SetStatus(System.Windows.Controls.TextBlock label, System.Windows.Shapes.Ellipse dot, StatusLine line)
    {
        label.Text = line.Text;
        dot.Fill = LevelBrush(line.Level);
    }

    /// <summary>Dot color from the controller's semantic level — locale-independent by
    /// construction (the previous display-text substring match broke under English strings).</summary>
    private static System.Windows.Media.Brush LevelBrush(StatusLevel level) => level switch
    {
        StatusLevel.Good => ThemeBrush("Brush.Success"),
        StatusLevel.InFlight => ThemeBrush("Brush.Warning"),
        _ => ThemeBrush("Brush.TextTertiary"),
    };

    private static System.Windows.Media.Brush ThemeBrush(string key) =>
        System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush
        ?? System.Windows.Media.Brushes.Transparent;

    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    /// <summary>Shows a fading glass toast above the status row (capture feedback).</summary>
    private void ShowToast(string message, bool success = true)
    {
        ToastText.Text = message;
        ToastIcon.Stroke = ThemeBrush(success ? "Brush.Success" : "Brush.Error");
        ToastPill.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
            ToastPill.Opacity, 1, TimeSpan.FromMilliseconds(150)));

        if (_toastTimer is null)
        {
            _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                ToastPill.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
                    ToastPill.Opacity, 0, TimeSpan.FromMilliseconds(350)));
            };
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------------------------------------------------------------- window chrome / session end

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int value = 1;
            User32.DwmSetWindowAttribute(hwnd, User32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch (Exception ex)
        {
            AppShellServices.LoggerFor<MainWindow>().LogDebug(ex, "Dark titlebar attribute failed");
        }
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_QUERYENDSESSION = 0x0011;
        const int WM_ENDSESSION = 0x0016;
        if (msg is WM_QUERYENDSESSION or WM_ENDSESSION)
        {
            // Windows is shutting down — the later Closing must NOT log off the child
            // session (same semantic as WinForms CloseReason != UserClosing). A
            // CANCELLED shutdown (WM_ENDSESSION, wParam FALSE) must restore the flag,
            // or a later user close would silently skip LogoffOnExit.
            _sessionEnding = wParam != IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>True when this close was user-initiated (X button or tray exit), false on OS shutdown.</summary>
    public bool IsUserInitiatedClose => !_sessionEnding;

    protected override void OnClosed(EventArgs e)
    {
        Loc.LanguageChanged -= OnLanguageChangedUi;
        _toastTimer?.Stop();
        UnbindController();
        base.OnClosed(e);
    }
}
