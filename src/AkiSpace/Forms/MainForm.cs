using System.Diagnostics;
using AkiSpace.App;
using AkiSpace.Common;
using AkiSpace.Controls;
using AkiSpace.Input;
using AkiSpace.Ipc;
using AkiSpace.Native;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Forms;

/// <summary>
/// AkiSpace main window — WinForms shell. All connect orchestration, tray,
/// hotkeys, agent launch and status polling live in the App layer
/// (<see cref="ConnectionController"/>, <see cref="TrayIconService"/>,
/// <see cref="HotkeyManager"/>); this class only builds the controls, renders
/// controller state and hosts the <see cref="RdpActiveXHost"/> in its viewer panel.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainForm> _logger;
    private readonly SettingsService _settingsService;
    private readonly ChildSessionManager _sessionManager;
    private readonly EnvironmentVerifier _environmentVerifier;
    private readonly ConnectionController _controller;
    private readonly TrayIconService _tray;
    private readonly HotkeyManager _hotkeys;

    // UI — standard controls
    private readonly Label _lblChildSession = new();
    private readonly Label _lblConnection = new();
    private readonly Label _lblWrapper = new();
    private readonly Label _lblPerformance = new();
    private readonly Button _btnConnect = new();
    private readonly Button _btnDisconnect = new();
    private readonly Button _btnTerminate = new();
    private readonly Button _btnGameMouse = new();
    private readonly Button _btnLaunch = new();
    private readonly Button _btnSetup = new();
    private readonly Button _btnSettings = new();
    private readonly Panel _viewerPanel = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new();

    private System.Windows.Forms.Timer? _statusTimer;
    private bool _closing;
    private RdpActiveXHost? _currentRdpHost;

    public MainForm(
        ILoggerFactory loggerFactory,
        SettingsService settingsService,
        ChildSessionManager sessionManager,
        EnvironmentVerifier environmentVerifier,
        ProcessLauncher processLauncher,
        PipeServer pipeServer,
        MouseForwarder mouseForwarder)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainForm>();
        _settingsService = settingsService;
        _sessionManager = sessionManager;
        _environmentVerifier = environmentVerifier;
        _controller = new ConnectionController(
            loggerFactory, settingsService, sessionManager, environmentVerifier,
            processLauncher, pipeServer, mouseForwarder,
            defer: action => BeginInvoke(action),
            promptForClonePassword: PromptForClonePassword,
            dialogOwner: () => this);
        _tray = new TrayIconService(loggerFactory.CreateLogger<TrayIconService>(), Icon);
        _hotkeys = new HotkeyManager(loggerFactory.CreateLogger<HotkeyManager>(), settingsService);

        BindController();

        BuildUi();
        Load += OnLoad;
        FormClosing += OnFormClosing;
    }

    private void BindController()
    {
        _controller.ConnectStateChanged += ApplyConnectState;
        _controller.ChildSessionStatusChanged += text => _lblChildSession.Text = text;
        _controller.WrapperStatusChanged += text => _lblWrapper.Text = text;
        _controller.PerformanceStatusChanged += text => _lblPerformance.Text = text;
        _controller.TrayConnectedChanged += connected => _tray.SetConnected(connected);
        _controller.RdpHostChanged += OnRdpHostChanged;
        _controller.FullScreenRequested += OnFullScreenRequested;
        _controller.LeaveFullScreenRequested += OnLeaveFullScreenRequested;

        _tray.ConnectToggleRequested += () => _controller.ToggleConnect();
        _tray.ShowWindowRequested += ShowMainWindow;
        _tray.ExitRequested += ExitApplication;

        _hotkeys.ToggleConnectRequested += () => _controller.ToggleConnect();
        _hotkeys.ShowWindowRequested += ShowMainWindow;
    }

    // ---------------------------------------------------------------- UI Construction

    private void BuildUi()
    {
        Text = "AkiSpace — 桌面分身";
        MinimumSize = new Size(960, 600);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = TextDark;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;

        // Status strip at bottom
        _statusStrip.BackColor = SurfaceDark;
        _statusStrip.ForeColor = TextSubtleDark;
        _statusStrip.SizingGrip = false;
        _statusLabel.Text = "就绪";
        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel("GitHub: AkiroMusic/AkiSpace")
        {
            IsLink = true,
            LinkColor = AccentDark,
            ActiveLinkColor = Color.FromArgb(129, 140, 248),
        });
        ((ToolStripStatusLabel)_statusStrip.Items[1]).Click += (_, _) =>
            Process.Start(new ProcessStartInfo("https://github.com/AkiroMusic/AkiSpace") { UseShellExecute = true });

        // Top panel: buttons
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = SurfaceDark,
            Height = 52,
        };

        // Buttons
        _btnConnect.Text = "连接";
        _btnConnect.Size = new Size(90, 36);
        _btnConnect.FlatStyle = FlatStyle.Flat;
        _btnConnect.BackColor = AccentDark;
        _btnConnect.ForeColor = Color.White;
        _btnConnect.Cursor = Cursors.Hand;
        _btnConnect.Click += async (_, _) => await _controller.ConnectAsync();

        _btnDisconnect.Text = "断开";
        _btnDisconnect.Size = new Size(90, 36);
        _btnDisconnect.FlatStyle = FlatStyle.Flat;
        _btnDisconnect.BackColor = SurfaceDark;
        _btnDisconnect.ForeColor = TextDark;
        _btnDisconnect.Cursor = Cursors.Hand;
        _btnDisconnect.Enabled = false;
        _btnDisconnect.Click += (_, _) => _controller.Disconnect();

        _btnTerminate.Text = "终止";
        _btnTerminate.Size = new Size(90, 36);
        _btnTerminate.FlatStyle = FlatStyle.Flat;
        _btnTerminate.BackColor = SurfaceDark;
        _btnTerminate.ForeColor = TextDark;
        _btnTerminate.Cursor = Cursors.Hand;
        _btnTerminate.Enabled = false;
        _btnTerminate.Click += (_, _) => _controller.TerminateChildSession();

        _btnGameMouse.Text = "游戏鼠标";
        _btnGameMouse.Size = new Size(100, 36);
        _btnGameMouse.FlatStyle = FlatStyle.Flat;
        _btnGameMouse.BackColor = SurfaceDark;
        _btnGameMouse.ForeColor = TextDark;
        _btnGameMouse.Cursor = Cursors.Hand;
        _btnGameMouse.Enabled = false;
        _btnGameMouse.Visible = false;
        _btnGameMouse.Click += (_, _) => _controller.ToggleGameMouse();

        _btnLaunch.Text = "启动程序";
        _btnLaunch.Size = new Size(100, 36);
        _btnLaunch.FlatStyle = FlatStyle.Flat;
        _btnLaunch.BackColor = SurfaceDark;
        _btnLaunch.ForeColor = TextDark;
        _btnLaunch.Cursor = Cursors.Hand;
        _btnLaunch.Enabled = false;
        _btnLaunch.Click += (_, _) => _controller.LaunchProgramInChildSession();

        _btnSetup.Text = "环境检查";
        _btnSetup.Size = new Size(100, 36);
        _btnSetup.FlatStyle = FlatStyle.Flat;
        _btnSetup.BackColor = SurfaceDark;
        _btnSetup.ForeColor = TextDark;
        _btnSetup.Cursor = Cursors.Hand;
        _btnSetup.Click += (_, _) => ShowSetupDialog();

        _btnSettings.Text = "设置";
        _btnSettings.Size = new Size(80, 36);
        _btnSettings.FlatStyle = FlatStyle.Flat;
        _btnSettings.BackColor = SurfaceDark;
        _btnSettings.ForeColor = TextDark;
        _btnSettings.Cursor = Cursors.Hand;
        _btnSettings.Click += (_, _) => ShowSettingsDialog();

        buttonPanel.Controls.AddRange(new Control[] {
            _btnConnect, _btnDisconnect, _btnTerminate,
            _btnGameMouse, _btnLaunch, _btnSetup, _btnSettings,
        });

        // Status labels panel
        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 1,
            Height = 32,
            Padding = new Padding(12, 4, 12, 4),
            BackColor = BgDark,
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        var labels = new[] { _lblChildSession, _lblConnection, _lblWrapper, _lblPerformance };
        var texts = new[] { "子会话: 检测中", "连接: 未连接", "RDP 解锁: 检测中", "CPU: 0% | 内存: 0 MB" };
        for (int i = 0; i < 4; i++)
        {
            labels[i].Text = texts[i];
            labels[i].AutoSize = true;
            labels[i].Font = new Font("Segoe UI", 8.5f);
            labels[i].ForeColor = TextSubtleDark;
            labels[i].BackColor = BgDark;
            labels[i].Dock = DockStyle.Top;
            statusPanel.Controls.Add(labels[i], i, 0);
        }

        // Viewer panel (RDP host goes here)
        _viewerPanel.Dock = DockStyle.Fill;
        _viewerPanel.BackColor = Color.Black;
        _viewerPanel.Padding = new Padding(2);

        // Layout: buttonPanel (top) + statusPanel (top) + viewerPanel (fill) + statusStrip (bottom)
        Controls.Add(_viewerPanel);
        Controls.Add(statusPanel);
        Controls.Add(buttonPanel);
        Controls.Add(_statusStrip);
    }

    // ---------------------------------------------------------------- Lifecycle

    private void OnLoad(object? sender, EventArgs e)
    {
        _logger.LogInformation("MainForm loaded");

        try
        {
            int value = 1;
            User32.DwmSetWindowAttribute(Handle, User32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { }

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => _controller.RefreshStatus();
        _statusTimer.Start();

        _controller.OnShellLoaded();

        _hotkeys.Register();

        if (_settingsService.Current.AutoConnect)
        {
            BeginInvoke(async () =>
            {
                try { await _controller.ConnectAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "AutoConnect failed"); }
            });
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _closing = true;
        _statusTimer?.Stop();
        _statusTimer?.Dispose();
        _statusTimer = null;

        // Forwarder off, RDP host teardown, and — only for explicit user closes —
        // the child-session logoff (never on the Windows-shutdown path).
        _controller.OnAppClosing(
            userInitiated: e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall);

        _hotkeys.Dispose();

        // A synchronous WTS logoff must not run on the OS-shutdown or task-manager
        // kill path — only log off when the user explicitly closed/exited the app.
        _tray.Dispose();

        _logger.LogInformation("MainForm closing");
    }

    // ---------------------------------------------------------------- Shell behaviour

    private void ApplyConnectState(ConnectUiState s)
    {
        _btnConnect.Enabled = s.ConnectEnabled;
        _btnDisconnect.Enabled = s.DisconnectEnabled;
        _btnTerminate.Enabled = s.TerminateEnabled;
        _btnGameMouse.Visible = s.GameMouseVisible;
        _btnGameMouse.Enabled = s.GameMouseEnabled;
        _btnGameMouse.Text = s.GameMouseText;
        _btnLaunch.Enabled = s.LaunchEnabled;
        _lblConnection.Text = s.ConnectionStatusText;
    }

    private void OnRdpHostChanged(RdpActiveXHost? host)
    {
        // Old TearDownRdpHost removed the host from the panel before disposing it;
        // RdpHostChanged(null) reproduces exactly that order.
        if (_currentRdpHost is not null)
            _viewerPanel.Controls.Remove(_currentRdpHost);
        _currentRdpHost = host;
        if (host is not null)
            _viewerPanel.Controls.Add(host);
    }

    private void OnFullScreenRequested()
    {
        BeginInvoke(() =>
        {
            WindowState = FormWindowState.Maximized;
            FormBorderStyle = FormBorderStyle.None;
        });
    }

    private void OnLeaveFullScreenRequested()
    {
        BeginInvoke(() =>
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
        });
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        _tray.SetVisible(false);
        Activate();
    }

    private void ExitApplication()
    {
        _closing = true;
        _controller.MarkClosing();
        // Tray/icon/timer/logoff cleanup happens once in OnFormClosing, which
        // Application.Exit re-enters.
        Application.Exit();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_closing) return;
        if (WindowState == FormWindowState.Minimized && _settingsService.Current.MinimizeToTray)
        {
            Hide();
            _tray.SetVisible(true);
        }
    }

    // ---------------------------------------------------------------- Dialogs

    private void ShowSetupDialog()
    {
        using var dialog = new SetupDialog(
            _loggerFactory.CreateLogger<SetupDialog>(),
            _environmentVerifier, _sessionManager);
        dialog.ShowDialog(this);
        _controller.RefreshStatus();
    }

    private void ShowSettingsDialog()
    {
        using var dialog = new SettingsDialog(_settingsService);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _controller.SetStatusText("连接: 设置已保存");
        }
    }

    /// <summary>Modal password prompt for the clone account (first Standard-RDP connect).</summary>
    private static string? PromptForClonePassword()
    {
        using var form = new Form
        {
            Text = "AkiSpace — 分身账户密码",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 150),
            Font = new Font("Segoe UI", 9f),
        };
        var label = new Label
        {
            Text = "标准 RDP 模式需要分身账户的密码。\nAkiSpace 不再内置默认密码，请输入分身账户的密码\n（将使用 DPAPI 加密保存在本机设置中）：",
            Location = new Point(12, 12),
            Size = new Size(396, 60),
        };
        var textBox = new TextBox
        {
            PasswordChar = '●',
            Location = new Point(12, 76),
            Size = new Size(396, 24),
        };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(240, 110), Size = new Size(80, 28) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(328, 110), Size = new Size(80, 28) };
        form.Controls.AddRange(new Control[] { label, textBox, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
    }

    // Dark theme colors
    private static readonly Color BgDark = Color.FromArgb(24, 24, 27);
    private static readonly Color SurfaceDark = Color.FromArgb(38, 38, 42);
    private static readonly Color AccentDark = Color.FromArgb(99, 102, 241);
    private static readonly Color TextDark = Color.FromArgb(228, 228, 231);
    private static readonly Color TextSubtleDark = Color.FromArgb(161, 161, 170);
}
