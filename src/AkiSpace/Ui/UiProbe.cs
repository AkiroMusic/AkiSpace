using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Forms.Integration;
using AkiSpace.Controls;
using AkiSpace.Native;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types we
// use so neither stack's names shadow the other.
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace AkiSpace.Ui;

/// <summary>
/// Phase-1 spike for the frontend rewrite: proves the MSTSC ActiveX
/// (<see cref="RdpActiveXHost"/>, an AxHost) hosts correctly inside a WPF window
/// via WindowsFormsHost — rendering, focus, event plumbing, DPI — before any
/// production UI is rewritten.
///
/// Diagnostic entry: <c>AkiSpace.exe --uiprobe [auto=host|connect] [delay=ms]</c>.
/// Does not touch the single-instance guard, pipe server or any service state;
/// it builds its own logger factory and writes akspace-uiprobe-*.log.
/// </summary>
internal static class UiProbe
{
    public static void Run(string[] args)
    {
        var auto = GetOption(args, "auto");          // "host" | "connect" | "deadport" | "deadport-wf" | absent
        var delayMs = 2000;
        if (int.TryParse(GetOption(args, "delay"), out var d) && d > 0) delayMs = d;

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AkiSpace", "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"akspace-uiprobe-{DateTime.Now:yyyyMMdd}.log");
        using var factory = LoggerFactory.Create(b =>
        {
            b.AddDebug();
            b.AddProvider(new FileLoggerProvider(logPath));
            b.SetMinimumLevel(LogLevel.Debug);
        });
        var logger = factory.CreateLogger("UiProbe");
        logger.LogInformation("UI probe starting (auto={Auto} delay={Delay}ms)", auto ?? "manual", delayMs);

        // Pure-WinForms control group: same AxHost, same dead-port connect, plain
        // Application.Run pump. Isolates WPF interop from mstscax behaviour.
        if (auto == "deadport-wf")
        {
            RunWinFormsControlGroup(factory, logger, delayMs);
            return;
        }

        // The WPF/WinForms interop contract: WinForms message-loop support must be
        // enabled before any WindowsFormsHost (and therefore AxHost) is created.
        System.Windows.Forms.Integration.WindowsFormsHost.EnableWindowsFormsInterop();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new ProbeWindow(factory, auto, delayMs);
        app.Run(window);
        logger.LogInformation("UI probe exited");
    }

    private static void RunWinFormsControlGroup(ILoggerFactory factory, ILogger logger, int delayMs)
    {
        var form = new System.Windows.Forms.Form
        {
            Text = "AkiSpace WF control group",
            Size = new System.Drawing.Size(900, 600),
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
        };
        var host = new RdpActiveXHost(factory.CreateLogger<RdpActiveXHost>());
        host.LoginCompleted += () => logger.LogInformation("[WF] LOGIN COMPLETE");
        host.ConnectionFailed += r => logger.LogInformation("[WF] CONNECTION FAILED: {Reason}", r.ReplaceLineEndings(" | "));
        form.Controls.Add(host);
        form.Shown += (_, _) =>
        {
            host.CreateControl();
            logger.LogInformation("[WF] host created, connecting dead port in {Delay}ms", delayMs);
            var timer = new System.Windows.Forms.Timer { Interval = delayMs };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                host.Connect(800, 600, 16, 9, true, false, false, "probe", "probe", false);
                logger.LogInformation("[WF] dead-port connect issued");
            };
            timer.Start();
        };
        System.Windows.Forms.Application.Run(form);
        logger.LogInformation("[WF] control group exited");
    }

    private static string? GetOption(string[] args, string name)
    {
        foreach (var a in args)
        {
            if (a.StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase))
                return a[(name.Length + 3)..];
        }
        return null;
    }
}

/// <summary>Code-only WPF window (no XAML) hosting the RDP ActiveX.</summary>
internal sealed class ProbeWindow : Window
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly string? _auto;
    private readonly int _delayMs;

    private readonly TextBlock _status = new();
    private readonly DockPanel _root = new();
    private readonly Grid _hostArea = new();

    private WindowsFormsHost? _formsHost;
    private RdpActiveXHost? _rdpHost;

    public ProbeWindow(ILoggerFactory loggerFactory, string? auto, int delayMs)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("UiProbe.Window");
        _auto = auto;
        _delayMs = delayMs;

        Title = "AkiSpace UI Probe";
        Width = 1280;
        Height = 800;
        MinWidth = 960;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B));

        BuildUi();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += (_, _) => OnLoaded();
    }

    private void BuildUi()
    {
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2A)),
        };
        toolbar.Children.Add(MakeButton("创建宿主 (WinFormsHost)", CreateHost));
        toolbar.Children.Add(MakeButton("连接分身", Connect));
        toolbar.Children.Add(MakeButton("断开", Disconnect));
        toolbar.Children.Add(MakeButton("关闭探针", () => Close()));

        DockPanel.SetDock(toolbar, Dock.Top);
        var statusBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2A)),
            Padding = new Thickness(12, 6, 12, 6),
            Child = _status,
        };
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        _status.Text = "就绪（spike：验证 AxHost 宿主）";
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA));

        _hostArea.Background = Brushes.Black;
        _hostArea.Margin = new Thickness(2);

        _root.Children.Add(toolbar);
        _root.Children.Add(statusBorder);
        _root.Children.Add(_hostArea);
        Content = _root;
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(8, 10, 0, 10),
            Padding = new Thickness(14, 6, 14, 6),
            MinWidth = 80,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

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
            _logger.LogDebug(ex, "Dark titlebar attribute failed");
        }
    }

    private void OnLoaded()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _logger.LogInformation("Probe loaded, WPF DPI={DpiX:F1}", dpi.DpiScaleX);

        // Heartbeat: proves the WPF dispatcher keeps pumping while the ActiveX is
        // connecting (COM connection-point events need this pump on the STA thread).
        var heartbeat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        heartbeat.Tick += (_, _) => _logger.LogDebug("probe pump alive");
        heartbeat.Start();

        switch (_auto)
        {
            case "host": RunDelayed(CreateHost); break;
            case "connect": RunDelayed(() => { CreateHost(); Connect(); }); break;
            case "deadport": RunDelayed(() => { CreateHost(); ConnectDeadPort(); }); break;
        }
    }

    private void RunDelayed(Action action)
    {
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            await System.Threading.Tasks.Task.Delay(_delayMs);
            action();
        });
    }

    private void SetStatus(string text)
    {
        Dispatcher.BeginInvoke(() => _status.Text = text);
        _logger.LogInformation("Status: {Text}", text);
    }

    private void CreateHost()
    {
        if (_rdpHost is not null) return;
        SetStatus("创建 RdpActiveXHost …");

        _rdpHost = new RdpActiveXHost(_loggerFactory.CreateLogger<RdpActiveXHost>());
        _rdpHost.LoginCompleted += () => SetStatus("LOGIN COMPLETE — 分身桌面就绪");
        _rdpHost.ConnectionFailed += reason => SetStatus("FAILED: " + reason.ReplaceLineEndings(" | "));
        _rdpHost.RequestedGoFullScreen += () => _logger.LogInformation("probe: remote requested fullscreen");
        _rdpHost.RequestedLeaveFullScreen += () => _logger.LogInformation("probe: remote requested leave fullscreen");

        _formsHost = new System.Windows.Forms.Integration.WindowsFormsHost { Child = _rdpHost };
        _hostArea.Children.Add(_formsHost);

        // Force WinForms control creation now (same as production: host.CreateControl()).
        _rdpHost.CreateControl();
        _logger.LogInformation("WindowsFormsHost created; rdpHandle={Handle}", _rdpHost.Handle);
        SetStatus("宿主已创建，句柄 " + _rdpHost.Handle);
    }

    private void Connect()
    {
        if (_rdpHost is null)
        {
            SetStatus("先创建宿主");
            return;
        }

        var settingsService = new SettingsService(_loggerFactory.CreateLogger<SettingsService>());
        var settings = settingsService.Current;
        var sessions = new ChildSessionManager(_loggerFactory.CreateLogger<ChildSessionManager>());
        _logger.LogInformation("Wrapper installed: {Wrapper}; termsrv {Ver}",
            sessions.IsRdpWrapperInstalled(), sessions.GetTermsrvVersion());

        var port = settings.RdpPort is > 0 and not 3389 ? settings.RdpPort : sessions.GetConfiguredRdpPort();
        SetStatus($"连接中 127.0.0.1:{port}（{settings.ConnectionMode}）…");

        try
        {
            if (settings.ConnectionMode == ConnectionMode.ChildSession)
            {
                sessions.EnableChildSessions();
                _rdpHost.ConnectToChildSession(
                    settings.DesktopWidth, settings.DesktopHeight, settings.ColorDepth,
                    port, settings.SmartSizing,
                    settings.SendSystemShortcutsToRemote, settings.AudioRedirected);
            }
            else
            {
                if (string.IsNullOrEmpty(settings.ClonePassword))
                {
                    SetStatus("标准 RDP 模式无密码（spike 不弹框），连接会失败 — 仅验证宿主");
                }
                _rdpHost.Connect(
                    settings.DesktopWidth, settings.DesktopHeight, settings.ColorDepth,
                    port, settings.SmartSizing,
                    settings.SendSystemShortcutsToRemote, settings.AudioRedirected,
                    userName: settings.CloneUsername, password: settings.ClonePassword,
                    useChildSession: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "probe connect threw");
            SetStatus("连接异常: " + ex.Message);
        }
    }

    /// <summary>
    /// Connects to a port with no listener (localhost:9 — discard, closed).
    /// The ActiveX gets an immediate refusal and should raise OnDisconnected →
    /// ConnectionFailed within seconds — the decisive check that COM connection-point
    /// events flow through the WPF/WinForms interop message loop.
    /// </summary>
    private void ConnectDeadPort()
    {
        if (_rdpHost is null) { SetStatus("先创建宿主"); return; }
        SetStatus("连接死端口 127.0.0.1:9（事件通路测试）…");
        _rdpHost.Connect(800, 600, 16, 9, smartSizing: true, keyboardHookToRemote: false,
            audioRedirected: false, userName: "probe", password: "probe", useChildSession: false);
    }

    private void Disconnect()
    {
        try
        {
            _rdpHost?.DisconnectSession();
            SetStatus("已断开");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "probe disconnect failed");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (_rdpHost is not null)
            {
                _rdpHost.DisconnectSession();
                _formsHost!.Child = null;
                _rdpHost.Dispose();
                _rdpHost = null;
                _formsHost = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "probe teardown");
        }
        base.OnClosed(e);
    }
}
