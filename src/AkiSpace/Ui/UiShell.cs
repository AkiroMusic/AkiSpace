using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AkiSpace.App;
using AkiSpace.Common;
using AkiSpace.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Application = System.Windows.Application;

namespace AkiSpace.Ui;

/// <summary>
/// WPF application shell: owns the <see cref="ConnectionController"/>, tray,
/// hotkeys, status timer and the <see cref="MainWindow"/>. The window renders
/// controller state; startup and shutdown sequencing lives here.
/// </summary>
internal sealed class UiShell
{
    private readonly ILogger<UiShell> _logger;
    private readonly SettingsService _settingsService;
    private readonly ConnectionController _controller;
    private readonly TrayIconService _tray;
    private readonly HotkeyManager _hotkeys;
    private readonly DispatcherTimer _statusTimer;
    private bool _shutdownDone;

    public MainWindow Main { get; }

    public UiShell(IServiceProvider provider)
    {
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger<UiShell>();
        _settingsService = provider.GetRequiredService<SettingsService>();

        var dispatcher = Application.Current.Dispatcher;
        var windowHandleOwner = new Win32OwnerWindow();

        _controller = new ConnectionController(
            provider.GetRequiredService<ILoggerFactory>(),
            _settingsService,
            provider.GetRequiredService<ChildSessionManager>(),
            provider.GetRequiredService<EnvironmentVerifier>(),
            provider.GetRequiredService<ProcessLauncher>(),
            provider.GetRequiredService<Ipc.PipeServer>(),
            provider.GetRequiredService<Input.MouseForwarder>(),
            defer: action => dispatcher.BeginInvoke(action),
            promptForClonePassword: PasswordPromptWindow.Prompt,
            dialogOwner: () => windowHandleOwner);

        Main = new MainWindow(_controller);
        windowHandleOwner.Attach(Main);

        _tray = new TrayIconService(loggerFactory.CreateLogger<TrayIconService>(), ExtractExeIcon());
        _tray.ConnectToggleRequested += () => _controller.ToggleConnect();
        _tray.ShowWindowRequested += ShowMainWindow;
        _tray.ScreenshotRequested += CaptureChildScreenToClipboard;
        _tray.ExitRequested += ExitApplication;

        _hotkeys = new HotkeyManager(loggerFactory.CreateLogger<HotkeyManager>(), _settingsService);
        _hotkeys.ToggleConnectRequested += () => _controller.ToggleConnect();
        _hotkeys.ShowWindowRequested += ShowMainWindow;
        _hotkeys.ScreenshotRequested += CaptureChildScreenToClipboard;
        // A settings save can flip the global-hotkey switch — apply it immediately.
        _settingsService.Changed += _hotkeys.ApplyEnabled;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => _controller.RefreshStatus();

        // A language switch re-renders XAML bindings automatically, but the
        // controller-emitted status strings are snapshots — re-emit them.
        Loc.LanguageChanged += () => dispatcher.Invoke(() =>
        {
            _controller.RefreshTexts();
            _controller.RefreshStatus();
        });

        TrySetWindowIcon(Main);

        Main.StateChanged += OnStateChanged;
        Main.Closing += OnClosing;
        Main.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Main window loaded");
        _controller.OnShellLoaded();
        _hotkeys.Register();
        _statusTimer.Start();

        if (_settingsService.Current.AutoConnect)
        {
            _ = Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try { await _controller.ConnectAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "AutoConnect failed"); }
            });
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_shutdownDone) return;
        if (Main.WindowState == WindowState.Minimized && _settingsService.Current.MinimizeToTray)
        {
            Main.Hide();
            _tray.SetVisible(true);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownDone) return;
        _shutdownDone = true;

        _statusTimer.Stop();

        // The watchdog MUST be armed before the destructive cleanup below: the MSTSC
        // ActiveX can block synchronously inside DisconnectSession/TearDownRdpHost
        // (observed: closing while a child-session connect is still negotiating hangs
        // the UI thread for good). With the watchdog armed first, any such hang still
        // terminates the process; on the healthy path the process exits before it fires.
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            Environment.Exit(0);
        });

        _controller.OnAppClosing(userInitiated: Main.IsUserInitiatedClose);
        _hotkeys.Dispose();
        _tray.Dispose();
        _logger.LogInformation("Shell shut down");

        // The MSTSC ActiveX can block window destruction while a connect is still
        // unwinding, leaving the dispatcher unable to finish shutdown. All durable
        // cleanup above has already run, so enforce termination with a watchdog;
        // on the healthy path the process exits normally before it fires.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
    }

    private void ShowMainWindow()
    {
        Main.Show();
        Main.WindowState = WindowState.Normal;
        _tray.SetVisible(false);
        Main.Activate();
    }

    /// <summary>
    /// Every capture entry (toolbar button, tray item, Ctrl+Shift+S) lands here.
    /// A minimized or hidden window renders nothing, so restore it first; failures
    /// are logged, never thrown — a capture must never take the session down.
    /// </summary>
    private void CaptureChildScreenToClipboard()
    {
        try
        {
            if (Main.WindowState == WindowState.Minimized)
            {
                Main.Show();
                Main.WindowState = WindowState.Normal;
            }
            Main.CaptureChildScreenToClipboard();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture child screen to clipboard failed");
        }
    }

    private void ExitApplication()
    {
        _controller.MarkClosing();
        Main.Close();
    }

    private static System.Drawing.Icon? ExtractExeIcon()
    {
        try
        {
            return Environment.ProcessPath is { } path
                ? System.Drawing.Icon.ExtractAssociatedIcon(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetWindowIcon(Window window)
    {
        try
        {
            var icon = ExtractExeIcon();
            if (icon is null) return;
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            window.Icon = source;
        }
        catch
        {
            // Icon is cosmetic — never block startup on it.
        }
    }

    /// <summary>IWin32Window over the main window's handle (for WinForms file dialogs).</summary>
    private sealed class Win32OwnerWindow : System.Windows.Forms.IWin32Window
    {
        private Window? _window;

        public IntPtr Handle =>
            _window is not null && new WindowInteropHelper(_window).Handle != IntPtr.Zero
                ? new WindowInteropHelper(_window).Handle
                : IntPtr.Zero;

        public void Attach(Window window) => _window = window;
    }
}
