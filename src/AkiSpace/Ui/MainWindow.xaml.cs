using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Forms.Integration;
using AkiSpace.App;
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

        SourceInitialized += (_, _) =>
        {
            ApplyDarkTitleBar();
            var source = (HwndSource)PresentationSource.FromVisual(this)!;
            source.AddHook(WndProcHook);
        };
    }

    private void BindController()
    {
        _controller.ConnectStateChanged += ApplyConnectState;
        _controller.ChildSessionStatusChanged += text => Dispatcher.Invoke(() => LblChildSession.Text = text);
        _controller.WrapperStatusChanged += text => Dispatcher.Invoke(() => LblWrapper.Text = text);
        _controller.PerformanceStatusChanged += text => Dispatcher.Invoke(() => LblPerformance.Text = text);
        _controller.RdpHostChanged += OnRdpHostChanged;
        _controller.FullScreenRequested += () => Dispatcher.Invoke(EnterFullScreen);
        _controller.LeaveFullScreenRequested += () => Dispatcher.Invoke(LeaveFullScreen);
    }

    private void ApplyConnectState(ConnectUiState s)
    {
        BtnConnect.IsEnabled = s.ConnectEnabled;
        BtnDisconnect.IsEnabled = s.DisconnectEnabled;
        BtnTerminate.IsEnabled = s.TerminateEnabled;
        BtnGameMouse.Visibility = s.GameMouseVisible ? Visibility.Visible : Visibility.Collapsed;
        BtnGameMouse.IsEnabled = s.GameMouseEnabled;
        BtnGameMouse.Content = s.GameMouseText;
        BtnLaunch.IsEnabled = s.LaunchEnabled;
        LblConnection.Text = s.ConnectionStatusText;
    }

    private void OnRdpHostChanged(RdpActiveXHost? host)
    {
        if (_formsHost is not null)
        {
            _formsHost.Child = null;
            ViewerArea.Children.Remove(_formsHost);
            _formsHost = null;
        }
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
        var log = AppShellServices.LoggerFor<MainWindow>();
        log.LogDebug("Opening settings window");
        try
        {
            var settings = new SettingsWindow(AppShellServices.Settings);
            settings.Owner = this;
            var result = settings.ShowDialog();
            log.LogDebug("Settings dialog closed, result={Result}", result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Settings dialog failed");
            throw;
        }
        _controller.SetStatusText("连接: 设置已保存");
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
            // session (same semantic as WinForms CloseReason != UserClosing).
            _sessionEnding = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>True when this close was user-initiated (X button or tray exit), false on OS shutdown.</summary>
    public bool IsUserInitiatedClose => !_sessionEnding;

    protected override void OnClosed(EventArgs e)
    {
        _controller.ConnectStateChanged -= ApplyConnectState;
        base.OnClosed(e);
    }
}
