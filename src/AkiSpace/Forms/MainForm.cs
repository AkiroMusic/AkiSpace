using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AkiSpace.Common;
using AkiSpace.Controls;
using AkiSpace.Controls.Styled;
using AkiSpace.Input;
using AkiSpace.Ipc;
using AkiSpace.Native;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Forms;

/// <summary>
/// AkiSpace main window - Ethereal Glass design:
/// Glass titlebar (40px) + Nav rail (80px) + Main content (cards) + Glass footer (28px)
/// Ambient background + noise overlay throughout.
/// </summary>
public sealed class MainForm : Form
{
    // Win32 P/Invoke for global hotkeys
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE_CONNECT = 1;
    private const int HOTKEY_SHOW_WINDOW = 2;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_ALT = 0x0001;
    private const uint VK_D = 0x44;
    private const uint VK_SPACE = 0x20;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainForm> _logger;
    private readonly SettingsService _settingsService;
    private readonly ChildSessionManager _sessionManager;
    private readonly EnvironmentVerifier _environmentVerifier;
    private readonly ProcessLauncher _processLauncher;
    private readonly CursorCapture _cursorCapture;
    private readonly MouseForwarder _mouseForwarder;
    private readonly AgentRunner _agentRunner;
    private readonly PipeServer _pipeServer;

    // UI - New Ethereal Glass layout
    private readonly AmbientBackground _ambientBg = new();
    private readonly NoiseOverlay _noiseOverlay = new();
    private readonly GlassSurface _titleBar = new();
    private readonly GlassSurface _footer = new();
    private readonly NavigationRail _navRail = new();
    private readonly Panel _mainContent = new();
    private readonly Panel _scrollableContent = new();
    private readonly FlowLayoutPanel _cardsContainer = new();

    // Titlebar elements
    private readonly Label _titleLabel = new();
    private readonly Button _btnMinimize = new();
    private readonly Button _btnClose = new();
    private readonly Button _btnThemeCycle = new();

    // Footer elements
    private readonly Label _footerCopyright = new();
    private readonly LinkLabel _footerLink = new();

    // Status cards (in main content)
    private readonly DoubleBezelCard _statusCard = new();
    private readonly Label _lblChildSession = new();
    private readonly Label _lblConnection = new();
    private readonly Label _lblWrapper = new();
    private readonly Label _lblPerformance = new();

    // Control card
    private readonly DoubleBezelCard _controlCard = new();
    private Panel _viewerPanel = new(); // RDP viewer panel
    private readonly PrimaryButton _btnConnect = new();
    private readonly GhostButton _btnDisconnect = new();
    private readonly GhostButton _btnTerminate = new();
    private readonly GhostButton _btnGameMouse = new();
    private readonly PrimaryButton _btnLaunch = new();
    private readonly GhostButton _btnSetup = new();
    private readonly GhostButton _btnSettings = new();

    // System tray
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;

    // RDP
    private RdpActiveXHost? _rdpHost;
    private System.Windows.Forms.Timer? _statusTimer;
    private bool _closing;
    private bool _isConnected;
    private bool _isFullscreen = false;
    private byte[]? _agentNonce;

    private string _cloneUsername => _settingsService.Current.CloneUsername;
    private string _clonePassword => _settingsService.Current.ClonePassword;

    public MainForm(
        ILoggerFactory loggerFactory,
        SettingsService settingsService,
        ChildSessionManager sessionManager,
        EnvironmentVerifier environmentVerifier,
        ProcessLauncher processLauncher,
        CursorCapture cursorCapture,
        AgentRunner agentRunner,
        PipeServer pipeServer,
        MouseForwarder mouseForwarder)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainForm>();
        _settingsService = settingsService;
        _sessionManager = sessionManager;
        _environmentVerifier = environmentVerifier;
        _processLauncher = processLauncher;
        _cursorCapture = cursorCapture;
        _agentRunner = agentRunner;
        _pipeServer = pipeServer;
        _mouseForwarder = mouseForwarder;

        // System tray
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Renderer = new ToolStripProfessionalRenderer(new TrayColorTable());
        _trayMenu.Items.Add("连接分身", null, (_, _) => ToggleConnect());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = Icon,
            Text = "AkiSpace - 未连接",
            ContextMenuStrip = _trayMenu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        BuildUi();
        Load += OnLoad;
        FormClosing += OnFormClosing;
        ThemeManager.Current.ThemeChanged += OnThemeChanged;
    }

    // ---------------------------------------------------------------- UI Construction

    private void BuildUi()
    {
        Text = "AkiSpace — 桌面分身";
        MinimumSize = new Size(960, 600);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ThemeManager.Current.BgBase;
        ForeColor = ThemeManager.Current.TextPrimary;
        Font = ThemeManager.Current.GetFontSans(13f);
        FormBorderStyle = FormBorderStyle.None; // Custom titlebar

        // Layer 0: Ambient background (z-index -1 equivalent)
        Controls.Add(_ambientBg);

        // Layer 1: Noise overlay (z-index 0)
        Controls.Add(_noiseOverlay);

        // Layer 2: Titlebar (40px, glass)
        BuildTitleBar();
        Controls.Add(_titleBar);

        // Layer 3: Main layout (Nav rail + Content)
        var mainLayout = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 40, 0, 28), // Titlebar 40px, Footer 28px
            BackColor = Color.Transparent,
        };
        BuildNavRail();
        BuildMainContent();
        mainLayout.Controls.Add(_mainContent);
        mainLayout.Controls.Add(_navRail);
        Controls.Add(mainLayout);

        // Layer 4: Footer (28px, glass)
        BuildFooter();
        Controls.Add(_footer);

        // Bring titlebar and footer to front
        _titleBar.BringToFront();
        _footer.BringToFront();

        // Subscribe to theme changes for titlebar/footer
        ThemeManager.Current.ThemeChanged += (_, _) =>
        {
            _titleBar.Invalidate();
            _footer.Invalidate();
            _navRail.Invalidate();
            Invalidate();
        };
    }

    private void BuildTitleBar()
    {
        _titleBar.Height = 40;
        _titleBar.Dock = DockStyle.Top;
        _titleBar.Padding = new Padding(16, 0, 16, 0);

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0),
        };

        // Title
        _titleLabel.Text = "AkiSpace";
        _titleLabel.AutoSize = true;
        _titleLabel.Font = ThemeManager.Current.GetFontDisplay(16f, FontStyle.Bold);
        _titleLabel.ForeColor = ThemeManager.Current.TextPrimary;
        _titleLabel.Margin = new Padding(0, 8, 0, 0);
        _titleLabel.BackColor = Color.Transparent;

        // Spacer
        var spacer = new Panel { Width = 1, Dock = DockStyle.Fill, BackColor = Color.Transparent };
        spacer.MinimumSize = new Size(100, 1);

        // Theme cycle button
        _btnThemeCycle.Size = new Size(32, 32);
        _btnThemeCycle.FlatStyle = FlatStyle.Flat;
        _btnThemeCycle.FlatAppearance.BorderSize = 0;
        _btnThemeCycle.BackColor = Color.Transparent;
        _btnThemeCycle.Cursor = Cursors.Hand;
        _btnThemeCycle.Margin = new Padding(8, 4, 0, 0);
        var themeToolTip = new ToolTip();
        themeToolTip.SetToolTip(_btnThemeCycle, "切换主题");
        _btnThemeCycle.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(ThemeManager.Current.TextSecondary, 1.7f);
            var cx = 16; var cy = 16; var r = 10;
            g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
            g.DrawLine(pen, cx, cy - r, cx, cy - r * 0.3f);
            g.DrawLine(pen, cx, cy + r, cx, cy + r * 0.3f);
            g.DrawLine(pen, cx - r, cy, cx - r * 0.3f, cy);
            g.DrawLine(pen, cx + r, cy, cx + r * 0.3f, cy);
        };
        _btnThemeCycle.Click += (_, _) => ThemeManager.Current.CycleTheme();
        _btnThemeCycle.MouseEnter += (_, _) => _btnThemeCycle.Invalidate();
        _btnThemeCycle.MouseLeave += (_, _) => _btnThemeCycle.Invalidate();

        // Minimize button
        _btnMinimize.Size = new Size(32, 32);
        _btnMinimize.FlatStyle = FlatStyle.Flat;
        _btnMinimize.FlatAppearance.BorderSize = 0;
        _btnMinimize.BackColor = Color.Transparent;
        _btnMinimize.Cursor = Cursors.Hand;
        _btnMinimize.Margin = new Padding(8, 4, 0, 0);
        _btnMinimize.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(_btnMinimize.BackColor.A > 0 ? ThemeManager.Current.Accent : ThemeManager.Current.TextSecondary, 1.7f);
            g.DrawLine(pen, 8, 16, 24, 16);
        };
        _btnMinimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _btnMinimize.MouseEnter += (_, _) => { _btnMinimize.BackColor = Color.FromArgb(30, ThemeManager.Current.TextPrimary); _btnMinimize.Invalidate(); };
        _btnMinimize.MouseLeave += (_, _) => { _btnMinimize.BackColor = Color.Transparent; _btnMinimize.Invalidate(); };

        // Close button
        _btnClose.Size = new Size(32, 32);
        _btnClose.FlatStyle = FlatStyle.Flat;
        _btnClose.FlatAppearance.BorderSize = 0;
        _btnClose.BackColor = Color.Transparent;
        _btnClose.Cursor = Cursors.Hand;
        _btnClose.Margin = new Padding(8, 4, 0, 0);
        _btnClose.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = _btnClose.BackColor.A > 0 ? Color.White : ThemeManager.Current.TextSecondary;
            using var pen = new Pen(color, 1.7f);
            g.DrawLine(pen, 10, 10, 22, 22);
            g.DrawLine(pen, 22, 10, 10, 22);
        };
        _btnClose.Click += (_, _) => ExitApplication();
        _btnClose.MouseEnter += (_, _) => { _btnClose.BackColor = Color.FromArgb(200, 232, 17, 35); _btnClose.Invalidate(); }; // #e81123
        _btnClose.MouseLeave += (_, _) => { _btnClose.BackColor = Color.Transparent; _btnClose.Invalidate(); };

        // Enable drag to move window
        _titleBar.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                User32.ReleaseCapture();
                User32.SendMessage(Handle, User32.WM_NCLBUTTONDOWN, (IntPtr)User32.HTCAPTION, IntPtr.Zero);
            }
        };
        _titleLabel.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                User32.ReleaseCapture();
                User32.SendMessage(Handle, User32.WM_NCLBUTTONDOWN, (IntPtr)User32.HTCAPTION, IntPtr.Zero);
            }
        };

        layout.Controls.Add(_titleLabel);
        layout.Controls.Add(spacer);
        layout.Controls.Add(_btnThemeCycle);
        layout.Controls.Add(_btnMinimize);
        layout.Controls.Add(_btnClose);
        _titleBar.Controls.Add(layout);
    }

    private void BuildNavRail()
    {
        _navRail.AddItem("main", "monitor", "概览", () => { /* Already on main */ });
        _navRail.AddItem("settings", "settings", "设置", () => ShowSettingsDialog());
        _navRail.AddItem("settings", "plug", "环境检查", () => ShowSetupDialog());
    }

    private void BuildMainContent()
    {
        _mainContent.Dock = DockStyle.Fill;
        _mainContent.BackColor = Color.Transparent;
        _mainContent.Padding = new Padding(ThemeTokens.Space.S8, ThemeTokens.Space.S8, ThemeTokens.Space.S6, ThemeTokens.Space.S6); // 32, 32, 24, 24

        _scrollableContent.Dock = DockStyle.Fill;
        _scrollableContent.AutoScroll = true;
        _scrollableContent.BackColor = Color.Transparent;

        _cardsContainer.Dock = DockStyle.Top;
        _cardsContainer.FlowDirection = FlowDirection.TopDown;
        _cardsContainer.WrapContents = false;
        _cardsContainer.AutoSize = true;
        _cardsContainer.BackColor = Color.Transparent;
        _cardsContainer.Padding = new Padding(0);

        _scrollableContent.Controls.Add(_cardsContainer);
        _mainContent.Controls.Add(_scrollableContent);

        // Status Card
        BuildStatusCard();
        _cardsContainer.Controls.Add(_statusCard);

        // Viewer Card (RDP)
        var viewerCard = new DoubleBezelCard
        {
            Title = "分身桌面",
            Subtitle = "RDP 内嵌查看器",
            Height = 480,
            Dock = DockStyle.Top,
        };
        viewerCard.ContentControls.Add(_viewerPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ThemeManager.Current.BgBase,
            Padding = new Padding(0),
        });
        _cardsContainer.Controls.Add(viewerCard);

        // Control Card
        BuildControlCard();
        _cardsContainer.Controls.Add(_controlCard);
    }

    private void BuildStatusCard()
    {
        _statusCard.Title = "连接状态";
        _statusCard.Subtitle = "实时监控分身会话状态";
        _statusCard.HeaderAction = CreateRefreshButton();

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        var labels = new[] { _lblChildSession, _lblConnection, _lblWrapper, _lblPerformance };
        var texts = new[] { "子会话: 检测中", "连接: 未连接", "RDP 解锁: 检测中", "CPU: 0% | 内存: 0 MB" };

        for (int i = 0; i < 4; i++)
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(0, 0, ThemeTokens.Space.S4, 0) };
            labels[i].Text = texts[i];
            labels[i].AutoSize = true;
            labels[i].Font = ThemeManager.Current.GetFontSans(13f);
            labels[i].ForeColor = ThemeManager.Current.TextPrimary;
            labels[i].BackColor = Color.Transparent;
            labels[i].Dock = DockStyle.Top;
            panel.Controls.Add(labels[i]);
            grid.Controls.Add(panel, i, 0);
        }

        _statusCard.ContentControls.Add(grid);
    }

    private Button CreateRefreshButton()
    {
        var btn = new Button
        {
            Size = new Size(28, 28),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
        };
        btn.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(ThemeManager.Current.TextTertiary, 1.7f);
            var cx = 14; var cy = 14; var r = 8;
            g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -90, 270);
            g.DrawLine(pen, cx + r * 0.7f, cy - r * 0.7f, cx + r, cy - r);
            g.DrawLine(pen, cx + r, cy - r, cx + r * 0.7f, cy - r * 1.3f);
        };
        btn.MouseEnter += (_, _) => { btn.Invalidate(); };
        btn.MouseLeave += (_, _) => { btn.Invalidate(); };
        btn.Click += (_, _) => RefreshStatus();
        return btn;
    }

    private void BuildControlCard()
    {
        _controlCard.Title = "控制面板";
        _controlCard.Subtitle = "连接管理与操作";

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, ThemeTokens.Space.S2, 0, 0),
        };

        // Primary: Connect
        _btnConnect.Text = "连接";
        _btnConnect.Size = new Size(100, 40);
        _btnConnect.Click += async (_, _) => await ConnectAsync();

        // Ghost: Disconnect
        _btnDisconnect.Text = "断开";
        _btnDisconnect.Size = new Size(100, 40);
        _btnDisconnect.Enabled = false;
        _btnDisconnect.Click += (_, _) => Disconnect();

        // Ghost: Terminate
        _btnTerminate.Text = "终止";
        _btnTerminate.Size = new Size(100, 40);
        _btnTerminate.Enabled = false;
        _btnTerminate.Click += (_, _) => TerminateChildSession();

        // Ghost: Game Mouse (shown only in standard RDP)
        _btnGameMouse.Text = "游戏鼠标";
        _btnGameMouse.Size = new Size(120, 40);
        _btnGameMouse.Enabled = false;
        _btnGameMouse.Visible = false;
        _btnGameMouse.Click += (_, _) => ToggleGameMouse();

        // Primary: Launch Program
        _btnLaunch.Text = "启动程序";
        _btnLaunch.Size = new Size(120, 40);
        _btnLaunch.Enabled = false;
        _btnLaunch.Click += (_, _) => LaunchProgramInChildSession();

        // Ghost: Setup
        _btnSetup.Text = "环境检查";
        _btnSetup.Size = new Size(120, 40);
        _btnSetup.Click += (_, _) => ShowSetupDialog();

        // Ghost: Settings
        _btnSettings.Text = "设置";
        _btnSettings.Size = new Size(100, 40);
        _btnSettings.Click += (_, _) => ShowSettingsDialog();

        flow.Controls.AddRange(new Control[] { _btnConnect, _btnDisconnect, _btnTerminate, _btnGameMouse, _btnLaunch, _btnSetup, _btnSettings });
        _controlCard.ContentControls.Add(flow);
    }

    private void BuildFooter()
    {
        _footer.Height = 28;
        _footer.Dock = DockStyle.Bottom;
        _footer.Padding = new Padding(16, 0, 16, 0);

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
        };

        _footerCopyright.Text = "© 2026 Akiro";
        _footerCopyright.AutoSize = true;
        _footerCopyright.Font = ThemeManager.Current.GetFontSans(11f);
        _footerCopyright.ForeColor = ThemeManager.Current.TextTertiary;
        _footerCopyright.BackColor = Color.Transparent;
        _footerCopyright.Margin = new Padding(0, 6, 0, 0);

        var spacer = new Panel { Width = 1, Dock = DockStyle.Fill, BackColor = Color.Transparent };
        spacer.MinimumSize = new Size(100, 1);

        _footerLink.Text = "GitHub";
        _footerLink.AutoSize = true;
        _footerLink.Font = ThemeManager.Current.GetFontSans(11f);
        _footerLink.LinkColor = ThemeManager.Current.Accent;
        _footerLink.ActiveLinkColor = ThemeManager.Current.AccentHover;
        _footerLink.VisitedLinkColor = ThemeManager.Current.AccentSecondary;
        _footerLink.BackColor = Color.Transparent;
        _footerLink.Margin = new Padding(0, 6, 0, 0);
        _footerLink.Click += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/akiro") { UseShellExecute = true });

        layout.Controls.Add(_footerCopyright);
        layout.Controls.Add(spacer);
        layout.Controls.Add(_footerLink);
        _footer.Controls.Add(layout);
    }

    // ---------------------------------------------------------------- Lifecycle

    private void OnLoad(object? sender, EventArgs e)
    {
        _logger.LogInformation("MainForm loaded");

        // Apply dark title bar for custom titlebar
        try
        {
            int value = 1;
            User32.DwmSetWindowAttribute(Handle, User32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { }

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        _mouseForwarder.Initialize(GetRdpViewerBounds, IsRdpInputFocused);
        _mouseForwarder.SetGameMouseModeEnabled(_settingsService.Current.GameMouseModeEnabled);

        RegisterGlobalHotkeys();
        RefreshStatus();

        if (_settingsService.Current.AutoConnect)
        {
            BeginInvoke(async () =>
            {
                try { await ConnectAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "AutoConnect failed"); }
            });
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _closing = true;
        _statusTimer?.Stop();
        _mouseForwarder.SetGameMouseModeEnabled(false);
        _rdpHost?.DisconnectSession();

        UnregisterGlobalHotkeys();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        ThemeManager.Current.ThemeChanged -= OnThemeChanged;

        if (_settingsService.Current.LogoffOnExit && _sessionManager.TryGetChildSessionId() is uint sid)
        {
            _sessionManager.LogoffChildSession(sid);
        }
        _logger.LogInformation("MainForm closing");
    }

    private void OnThemeChanged(object? sender, string themeName)
    {
        // Refresh all custom controls
        BackColor = ThemeManager.Current.BgBase;
        ForeColor = ThemeManager.Current.TextPrimary;
        Font = ThemeManager.Current.GetFontSans(13f);

        _titleBar.Invalidate();
        _footer.Invalidate();
        _navRail.Invalidate();
        _statusCard.Invalidate();
        _controlCard.Invalidate();

        // Update status labels fonts/colors
        foreach (var lbl in new[] { _lblChildSession, _lblConnection, _lblWrapper, _lblPerformance, _titleLabel, _footerCopyright })
        {
            lbl.Font = ThemeManager.Current.GetFontSans(lbl.Font.Size);
            lbl.ForeColor = ThemeManager.Current.TextPrimary;
        }
        _footerLink.LinkColor = ThemeManager.Current.Accent;
        _footerLink.ActiveLinkColor = ThemeManager.Current.AccentHover;
        _footerLink.VisitedLinkColor = ThemeManager.Current.AccentSecondary;

        Invalidate();
    }

    // ---------------------------------------------------------------- Global Hotkeys

    private void RegisterGlobalHotkeys()
    {
        if (!_settingsService.Current.EnableGlobalHotkey)
        {
            _logger.LogInformation("Global hotkeys disabled by settings");
            return;
        }
        try
        {
            if (!RegisterHotKey(Handle, HOTKEY_TOGGLE_CONNECT, MOD_CONTROL | MOD_SHIFT, VK_D))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning("Failed to register hotkey Ctrl+Shift+D (Win32 error {Error}); may be in use", err);
            }

            if (!RegisterHotKey(Handle, HOTKEY_SHOW_WINDOW, MOD_CONTROL | MOD_ALT, VK_SPACE))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning("Failed to register hotkey Ctrl+Alt+Space (Win32 error {Error}); may be in use", err);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register global hotkeys");
        }
    }

    private void UnregisterGlobalHotkeys()
    {
        try
        {
            UnregisterHotKey(Handle, HOTKEY_TOGGLE_CONNECT);
            UnregisterHotKey(Handle, HOTKEY_SHOW_WINDOW);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister global hotkeys");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            HandleHotkey(m.WParam.ToInt32());
        }
        base.WndProc(ref m);
    }

    private void HandleHotkey(int hotkeyId)
    {
        switch (hotkeyId)
        {
            case HOTKEY_TOGGLE_CONNECT:
                ToggleConnect();
                break;
            case HOTKEY_SHOW_WINDOW:
                ShowMainWindow();
                break;
        }
    }

    // ---------------------------------------------------------------- Tray Icon

    private void ToggleConnect()
    {
        if (_isConnected)
        {
            Disconnect();
        }
        else
        {
            BeginInvoke(async () =>
            {
                try { await ConnectAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "Connect from hotkey/tray failed"); }
            });
        }
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        _trayIcon.Visible = false;
        Activate();
    }

    private void ExitApplication()
    {
        _closing = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _settingsService.Current.MinimizeToTray)
        {
            Hide();
            _trayIcon.Visible = true;
            _trayIcon.Text = _isConnected ? "AkiSpace - 已连接" : "AkiSpace - 未连接";
        }
    }

    // ---------------------------------------------------------------- Actions

    private async Task ConnectAsync()
    {
        try
        {
            _lblConnection.Text = "连接: 正在准备...";

            var settings = _settingsService.Current;

            if (settings.ClonePassword == "lb33")
            {
                _logger.LogWarning("ClonePassword is the built-in default 'lb33' — change it in Settings before connecting to a real account");
            }

            if (settings.ConnectionMode != ConnectionMode.ChildSession
                && !await _sessionManager.IsRdpListenerActiveAsync())
            {
                MessageBox.Show(
                    "RDP 监听器暂时未响应连接探测。可能原因：\n" +
                    "1) TermService 正在重启（禁用 RDP Wrapper 后需要 10–20 秒）— 请稍等再试\n" +
                    "2) 真未监听 — 运行「环境检查/修复」→「一键修复」\n" +
                    "3) 防火墙/杀软拦截 127.0.0.1:3389",
                    "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _lblConnection.Text = "连接: RDP 未就绪";
                return;
            }

            _viewerPanel.Controls.Clear();
            _rdpHost = new RdpActiveXHost(_loggerFactory.CreateLogger<RdpActiveXHost>());
            _rdpHost.LoginCompleted += OnRdpLoginComplete;
            _rdpHost.ConnectionFailed += OnRdpConnectionFailed;
            _rdpHost.RequestedGoFullScreen += OnRdpRequestFullScreen;
            _rdpHost.RequestedLeaveFullScreen += OnRdpRequestLeaveFullScreen;
            _viewerPanel.Controls.Add(_rdpHost);

            _rdpHost.CreateControl();

            var port = settings.RdpPort is > 0 and not 3389
                ? settings.RdpPort
                : _sessionManager.GetConfiguredRdpPort();

            _lblConnection.Text = $"连接: 正在连接 127.0.0.1:{port} ...";

            BeginInvoke(new Action(() => BeginInvoke(new Action(() =>
            {
                if (_rdpHost is null || _rdpHost.IsDisposed) return;

                if (settings.ConnectionMode == ConnectionMode.ChildSession)
                {
                    var hookCheck = _environmentVerifier.CheckRdpWrapperHook();
                    if (!hookCheck.Pass)
                    {
                        _logger.LogWarning("RDP Wrapper hook detected; child session will fail: {Detail}", hookCheck.Detail);
                        MessageBox.Show(
                            "检测到 RDP Wrapper (TermWrap.dll) 已 hook TermService。\n\n"
                            + "BetterGI 官方文档明确说明：RDP Wrapper 与桌面分身（子会话）功能不兼容，"
                            + "两者不能同时使用。RDP Wrapper 的 hook 会导致子会话 broker 无法创建会话，"
                            + "表现为「远程桌面无法连接到远程计算机 (516)」。\n\n"
                            + "请按以下步骤解决：\n"
                            + "1. 在 AkiSpace 中打开「环境检查/修复」\n"
                            + "2. 点击「一键修复」（最新版会禁用 RDP Wrapper 并恢复 termsrv.dll）\n"
                            + "3. 等待 TermService 重启完成\n"
                            + "4. 重新尝试子会话连接\n\n"
                            + "RDP Wrapper 和 AkiSpace 子会话功能重复（二者都是为多用户场景设计），"
                            + "只能二选一。建议直接使用 AkiSpace 子会话。",
                            "AkiSpace — RDP Wrapper 与子会话冲突",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }
                }

                if (settings.ConnectionMode == ConnectionMode.ChildSession)
                {
                    var enableResult = _sessionManager.EnableChildSessions();
                    _logger.LogInformation("WTSEnableChildSessions(true) before connect returned {Result}", enableResult);
                }

                if (settings.ConnectionMode == ConnectionMode.ChildSession)
                {
                    _rdpHost.ConnectToChildSession(
                        settings.DesktopWidth, settings.DesktopHeight, settings.ColorDepth,
                        port, settings.SmartSizing,
                        settings.SendSystemShortcutsToRemote, settings.AudioRedirected);
                }
                else
                {
                    _rdpHost.Connect(
                        settings.DesktopWidth, settings.DesktopHeight, settings.ColorDepth,
                        port, settings.SmartSizing,
                        settings.SendSystemShortcutsToRemote, settings.AudioRedirected,
                        userName: _cloneUsername, password: _clonePassword,
                        useChildSession: false);
                }
            }))));

            _btnConnect.Enabled = false;
            _btnDisconnect.Enabled = true;
            _btnTerminate.Enabled = true;
            var enableGameMouse = settings.ConnectionMode != ConnectionMode.ChildSession;
            _btnGameMouse.Enabled = enableGameMouse;
            _btnGameMouse.Visible = enableGameMouse;
            _btnLaunch.Enabled = true;
            _isConnected = true;
            UpdateTrayIcon(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connect failed");
            _lblConnection.Text = "连接: 连接失败";
            MessageBox.Show($"连接失败：{ex.Message}", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Disconnect()
    {
        _rdpHost?.DisconnectSession();
        _btnConnect.Enabled = true;
        _btnDisconnect.Enabled = false;
        _btnGameMouse.Enabled = false;
        _btnLaunch.Enabled = false;
        _mouseForwarder.SetGameMouseModeEnabled(false);
        _lblConnection.Text = "连接: 已断开";
        _isConnected = false;
        UpdateTrayIcon(false);
    }

    private void UpdateTrayIcon(bool connected)
    {
        _trayIcon.Text = connected ? "AkiSpace - 已连接" : "AkiSpace - 未连接";
        _trayMenu.Items[0].Text = connected ? "断开分身" : "连接分身";
    }

    private void TerminateChildSession()
    {
        var sid = _sessionManager.TryGetChildSessionId();
        if (sid is null)
        {
            MessageBox.Show("当前没有活动的子会话。", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var confirm = MessageBox.Show(
            $"确定要终止子会话（ID {sid.Value}）吗？其中的程序将全部关闭。",
            "AkiSpace", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        if (_sessionManager.LogoffChildSession(sid.Value))
        {
            _rdpHost?.DisconnectSession();
            _viewerPanel.Controls.Clear();
            _rdpHost = null;
            _btnConnect.Enabled = true;
            _btnDisconnect.Enabled = false;
            _btnTerminate.Enabled = false;
            _btnGameMouse.Enabled = false;
            _btnLaunch.Enabled = false;
            _mouseForwarder.SetGameMouseModeEnabled(false);
            _lblConnection.Text = "连接: 子会话已终止";
        }
        else
        {
            _lblConnection.Text = "连接: 终止失败";
        }
    }

    private void ToggleGameMouse()
    {
        var enabled = _mouseForwarder.IsGameMouseModeEnabled;
        if (!enabled && !_mouseForwarder.IsAgentConnected)
        {
            MessageBox.Show(
                "回放 Agent 未连接。游戏鼠标模式需要分身侧运行回放 Agent（--agent 模式）。\n" +
                "请先在分身会话中启动 Agent，或在 Agent 落地前使用标准 RDP 鼠标。",
                "AkiSpace — 游戏鼠标", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _mouseForwarder.SetGameMouseModeEnabled(!enabled);
        _btnGameMouse.Text = enabled ? "游戏鼠标" : "游戏鼠标: 开";
        _settingsService.Update(s => s.GameMouseModeEnabled = !enabled);
    }

    /// <summary>
    /// Arms the pipe server with a nonce and launches the agent in the child session.
    /// </summary>
    private void LaunchAgentInChildSession()
    {
        try
        {
            _agentNonce = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(_agentNonce);
            var nonceHex = Convert.ToHexString(_agentNonce).ToLowerInvariant();

            _pipeServer.SetNonce(_agentNonce);

            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath is null)
            {
                _logger.LogWarning("Cannot determine exe path for agent launch");
                return;
            }

            var sid = _sessionManager.TryGetChildSessionId();
            if (sid is null)
            {
                _logger.LogWarning("No child session id — agent not launched");
                return;
            }

            var args = $"--agent --nonce {nonceHex}";
            if (_processLauncher.LaunchInChildSession(exePath, sid.Value, args))
            {
                _logger.LogInformation("Agent launched in child session {Sid} (nonce={Nonce})", sid.Value, nonceHex[..16] + "...");
                _lblConnection.Text = "连接: 已连接 — Agent 启动中";
            }
            else
            {
                _logger.LogWarning("Agent launch failed in child session {Sid}", sid.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch agent in child session");
        }
    }

    private void LaunchProgramInChildSession()
    {
        var sid = _sessionManager.TryGetChildSessionId();
        if (sid is null)
        {
            MessageBox.Show("请先连接分身。", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var ofd = new OpenFileDialog
        {
            Title = "选择要在分身会话中启动的程序",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        if (_processLauncher.LaunchInChildSession(ofd.FileName, sid.Value))
        {
            _lblConnection.Text = $"连接: 已在分身（会话 {sid.Value}）中启动 {Path.GetFileName(ofd.FileName)}";
        }
        else
        {
            _lblConnection.Text = "连接: 启动失败（详见日志）";
            MessageBox.Show("启动失败。可能是权限不足或 Task Scheduler 服务不可用。", "AkiSpace",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowSetupDialog()
    {
        using var dialog = new SetupDialog(
            _loggerFactory.CreateLogger<SetupDialog>(),
            _environmentVerifier, _sessionManager);
        dialog.ShowDialog(this);
        RefreshStatus();
    }

    private void ShowSettingsDialog()
    {
        using var dialog = new SettingsDialog(_settingsService);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _lblConnection.Text = "连接: 设置已保存";
        }
    }

    // ---------------------------------------------------------------- RDP Events

    private void OnRdpLoginComplete()
    {
        if (_closing) return;
        _logger.LogInformation("RDP login complete event");
        _lblConnection.Text = "连接: 已连接 — 分身桌面就绪";
        _btnGameMouse.Enabled = true;
        _isConnected = true;
        UpdateTrayIcon(true);

        BeginInvoke(() => _rdpHost?.TryFocusRdpInputWindow());

        // Launch the agent in the child session for mouse replay.
        if (_settingsService.Current.ConnectionMode != ConnectionMode.ChildSession)
        {
            LaunchAgentInChildSession();
        }

        var launchPath = _settingsService.Current.LaunchProgramPath;
        if (!string.IsNullOrWhiteSpace(launchPath))
        {
            var sid = _sessionManager.TryGetChildSessionId();
            if (sid.HasValue)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (_processLauncher.LaunchInChildSession(launchPath, sid.Value))
                            _logger.LogInformation("Auto-launched {Exe} in child session {Sid}", launchPath, sid.Value);
                        else
                            _logger.LogWarning("Auto-launch failed for {Exe} in child session {Sid}", launchPath, sid.Value);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Auto-launch threw for {Exe}", launchPath);
                    }
                });
            }
            else
            {
                _logger.LogInformation("Auto-launch skipped: no child session id available");
            }
        }
    }

    private void OnRdpConnectionFailed(string reason)
    {
        if (_closing) return;
        _logger.LogWarning("RDP connection failed: {Reason}", reason);
        _lblConnection.Text = "连接: 连接失败";
        _btnConnect.Enabled = true;
        _btnDisconnect.Enabled = false;
        _btnGameMouse.Enabled = false;
        _btnLaunch.Enabled = false;
        _isConnected = false;
        UpdateTrayIcon(false);
        MessageBox.Show(reason, "AkiSpace 连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void OnRdpRequestFullScreen()
    {
        if (_closing) return;
        _logger.LogInformation("Remote requested fullscreen");
        _isFullscreen = true;
        BeginInvoke(() =>
        {
            WindowState = FormWindowState.Maximized;
            FormBorderStyle = FormBorderStyle.None;
        });
    }

    private void OnRdpRequestLeaveFullScreen()
    {
        if (_closing) return;
        _logger.LogInformation("Remote requested leave fullscreen");
        _isFullscreen = false;
        BeginInvoke(() =>
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
        });
    }

    // ---------------------------------------------------------------- Status Refresh

    private void RefreshStatus()
    {
        if (_closing || !IsHandleCreated) return;

        try
        {
            var sid = _sessionManager.TryGetChildSessionId();
            _lblChildSession.Text = sid.HasValue
                ? $"子会话: 活动 (ID {sid.Value})"
                : "子会话: 无";

            _lblWrapper.Text = _sessionManager.IsRdpWrapperInstalled()
                ? "RDP 解锁: 已安装"
                : "RDP 解锁: 未安装";

            if (_rdpHost != null)
            {
                var connected = _rdpHost.GetConnectedState();
                _lblConnection.Text = connected ? "连接: 已连接" : "连接: 未连接";
                _isConnected = connected;
                UpdateTrayIcon(connected);
            }

            if (_settingsService.Current.ShowPerformance)
            {
                var process = Process.GetCurrentProcess();
                var elapsed = DateTime.Now - process.StartTime;
                var cpuUsage = elapsed.TotalMilliseconds > 0
                    ? process.TotalProcessorTime.TotalMilliseconds / elapsed.TotalMilliseconds * 100
                    : 0;
                var memoryMB = process.WorkingSet64 / 1024 / 1024;
                _lblPerformance.Text = $"CPU: {cpuUsage:F1}% | 内存: {memoryMB} MB";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshStatus error");
        }
    }

    // ---------------------------------------------------------------- Forwarder Callbacks

    private User32.RECT GetRdpViewerBounds()
    {
        if (_rdpHost == null || !_rdpHost.IsHandleCreated)
            return default;
        User32.GetWindowRect(_rdpHost.Handle, out var rect);
        return rect;
    }

    private bool IsRdpInputFocused()
    {
        if (_rdpHost == null || !_rdpHost.IsHandleCreated)
            return false;
        return _rdpHost.IsRdpInputWindowFocused();
    }

    // ---------------------------------------------------------------- Tray Color Table

    private sealed class TrayColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => ThemeManager.Current.Border;
        public override Color MenuItemBorder => ThemeManager.Current.Border;
        public override Color MenuItemSelected => Color.FromArgb(33, ThemeManager.Current.Accent.R, ThemeManager.Current.Accent.G, ThemeManager.Current.Accent.B);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(33, ThemeManager.Current.Accent.R, ThemeManager.Current.Accent.G, ThemeManager.Current.Accent.B);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(33, ThemeManager.Current.Accent.R, ThemeManager.Current.Accent.G, ThemeManager.Current.Accent.B);
        public override Color MenuItemPressedGradientBegin => ThemeManager.Current.Accent;
        public override Color MenuItemPressedGradientEnd => ThemeManager.Current.Accent;
        public override Color ToolStripDropDownBackground => ThemeManager.Current.Surface1;
        public override Color ImageMarginGradientBegin => ThemeManager.Current.Surface1;
        public override Color ImageMarginGradientEnd => ThemeManager.Current.Surface1;
        public override Color ImageMarginGradientMiddle => ThemeManager.Current.Surface1;
    }
}