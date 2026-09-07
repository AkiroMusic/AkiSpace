using System.Diagnostics;
using System.Runtime.InteropServices;
using AkiSpace.Common;
using AkiSpace.Controls;
using AkiSpace.Input;
using AkiSpace.Ipc;
using AkiSpace.Native;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Forms;

/// <summary>
/// AkiSpace main window: status bar + embedded RDP viewer + control bar.
/// Supports system tray, global hotkeys, and minimize-to-tray.
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

    // Modifier keys
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_ALT = 0x0001;

    // Virtual key codes
    private const uint VK_D = 0x44; // D key
    private const uint VK_SPACE = 0x20; // Space key

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainForm> _logger;
    private readonly SettingsService _settingsService;
    private readonly ChildSessionManager _sessionManager;
    private readonly EnvironmentVerifier _environmentVerifier;
    private readonly ProcessLauncher _processLauncher;
    private readonly CursorCapture _cursorCapture;
    private readonly KeyboardHandler _keyboardHandler;
    private readonly MouseForwarder _mouseForwarder;

    // UI
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

    // System tray
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;

    private RdpActiveXHost? _rdpHost;
    private System.Windows.Forms.Timer? _statusTimer;
    private bool _closing;
    private bool _isConnected;

    private string _cloneUsername => _settingsService.Current.CloneUsername;
    private string _clonePassword => _settingsService.Current.ClonePassword;

    public MainForm(
        ILoggerFactory loggerFactory,
        SettingsService settingsService,
        ChildSessionManager sessionManager,
        EnvironmentVerifier environmentVerifier,
        ProcessLauncher processLauncher,
        CursorCapture cursorCapture,
        KeyboardHandler keyboardHandler,
        MouseForwarder mouseForwarder)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainForm>();
        _settingsService = settingsService;
        _sessionManager = sessionManager;
        _environmentVerifier = environmentVerifier;
        _processLauncher = processLauncher;
        _cursorCapture = cursorCapture;
        _keyboardHandler = keyboardHandler;
        _mouseForwarder = mouseForwarder;

        // Initialize system tray icon
        _trayMenu = new ContextMenuStrip();
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
    }

    // ---------------------------------------------------------------- UI construction

    private void BuildUi()
    {
        Text = "AkiSpace — 桌面分身";
        MinimumSize = new Size(960, 600);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);

        // --- Status bar (top) ---
        var statusPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Theme.Surface,
            BorderStyle = BorderStyle.None
        };
        var statusLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0),
            BackColor = Theme.Surface,
        };
        statusLayout.Controls.Add(MakeStatusLabel(_lblChildSession, "子会话: 检测中"));
        statusLayout.Controls.Add(MakeStatusLabel(_lblConnection, "连接: 未连接"));
        statusLayout.Controls.Add(MakeStatusLabel(_lblWrapper, "RDP 解锁: 检测中"));
        statusLayout.Controls.Add(MakeStatusLabel(_lblPerformance, "CPU: 0% | 内存: 0 MB"));
        statusPanel.Controls.Add(statusLayout);

        // --- Control bar (bottom) ---
        var controlPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Theme.Surface
        };
        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0),
            BackColor = Theme.Surface,
        };

        _btnConnect.Text = "连接";
        _btnConnect.Size = new Size(70, 36);
        _btnConnect.FlatStyle = FlatStyle.Flat;
        _btnConnect.BackColor = Theme.AccentBlue;
        _btnConnect.ForeColor = Color.White;
        _btnConnect.FlatAppearance.BorderSize = 0;
        _btnConnect.Click += async (_, _) => await ConnectAsync();

        _btnDisconnect.Text = "断开";
        _btnDisconnect.Size = new Size(70, 36);
        _btnDisconnect.FlatStyle = FlatStyle.Flat;
        _btnDisconnect.BackColor = Theme.Control;
        _btnDisconnect.ForeColor = Theme.Text;
        _btnDisconnect.FlatAppearance.BorderColor = Theme.Border;
        _btnDisconnect.Enabled = false;
        _btnDisconnect.Click += (_, _) => Disconnect();

        _btnTerminate.Text = "终止";
        _btnTerminate.Size = new Size(70, 36);
        _btnTerminate.FlatStyle = FlatStyle.Flat;
        _btnTerminate.BackColor = Theme.Control;
        _btnTerminate.ForeColor = Theme.AccentRed;
        _btnTerminate.FlatAppearance.BorderColor = Theme.Border;
        _btnTerminate.Enabled = false;
        _btnTerminate.Click += (_, _) => TerminateChildSession();

        _btnGameMouse.Text = "游戏鼠标";
        _btnGameMouse.Size = new Size(90, 36);
        _btnGameMouse.FlatStyle = FlatStyle.Flat;
        _btnGameMouse.BackColor = Theme.Control;
        _btnGameMouse.ForeColor = Theme.Text;
        _btnGameMouse.FlatAppearance.BorderColor = Theme.Border;
        _btnGameMouse.Enabled = false;
        _btnGameMouse.Visible = _settingsService.Current.ConnectionMode != ConnectionMode.ChildSession;
        _btnGameMouse.Click += (_, _) => ToggleGameMouse();

        _btnLaunch.Text = "启动程序";
        _btnLaunch.Size = new Size(90, 36);
        _btnLaunch.FlatStyle = FlatStyle.Flat;
        _btnLaunch.BackColor = Theme.Control;
        _btnLaunch.ForeColor = Theme.Text;
        _btnLaunch.FlatAppearance.BorderColor = Theme.Border;
        _btnLaunch.Enabled = false;
        _btnLaunch.Click += (_, _) => LaunchProgramInChildSession();

        _btnSetup.Text = "环境检查";
        _btnSetup.Size = new Size(90, 36);
        _btnSetup.FlatStyle = FlatStyle.Flat;
        _btnSetup.BackColor = Theme.Control;
        _btnSetup.ForeColor = Theme.Text;
        _btnSetup.FlatAppearance.BorderColor = Theme.Border;
        _btnSetup.Click += (_, _) => ShowSetupDialog();

        _btnSettings.Text = "设置";
        _btnSettings.Size = new Size(70, 36);
        _btnSettings.FlatStyle = FlatStyle.Flat;
        _btnSettings.BackColor = Theme.Control;
        _btnSettings.ForeColor = Theme.Text;
        _btnSettings.FlatAppearance.BorderColor = Theme.Border;
        _btnSettings.Click += (_, _) => ShowSettingsDialog();

        controls.Controls.AddRange(new Control[] { _btnConnect, _btnDisconnect, _btnTerminate, _btnGameMouse, _btnLaunch, _btnSetup, _btnSettings });
        controlPanel.Controls.Add(controls);

        // --- Viewer (fills remaining space) ---
        _viewerPanel.Dock = DockStyle.Fill;
        _viewerPanel.BackColor = Theme.Bg;
        _viewerPanel.Padding = new Padding(0);

        // --- Status strip (very bottom) ---
        _statusStrip.BackColor = Theme.Surface;
        _statusStrip.ForeColor = Theme.TextDim;
        _statusStrip.Items.Add(_statusLabel);
        _statusLabel.Text = "就绪";

        Controls.Add(_viewerPanel);
        Controls.Add(statusPanel);
        Controls.Add(controlPanel);
        Controls.Add(_statusStrip);
    }

    private static Label MakeStatusLabel(Label label, string text)
    {
        label.Text = text;
        label.AutoSize = true;
        label.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        label.ForeColor = Theme.Text;
        label.BackColor = Theme.Surface;
        label.Margin = new Padding(0, 0, 24, 0);
        return label;
    }

    // ---------------------------------------------------------------- lifecycle

    private void OnLoad(object? sender, EventArgs e)
    {
        _logger.LogInformation("MainForm loaded");

        // Apply dark title bar
        try
        {
            int value = 1;
            User32.DwmSetWindowAttribute(Handle, User32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { /* DWM not available on older Windows */ }

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        // Wire mouse forwarder to the UI callbacks (deferred until RDP host exists)
        _mouseForwarder.Initialize(GetRdpViewerBounds, IsRdpInputFocused);
        _mouseForwarder.SetGameMouseModeEnabled(_settingsService.Current.GameMouseModeEnabled);

        // Register global hotkeys
        RegisterGlobalHotkeys();

        RefreshStatus();

        if (_settingsService.Current.AutoConnect)
        {
            // Wrap the async-void in try/catch so any unobserved exception is
            // routed to the logger instead of crashing the UI thread.
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

        // Unregister global hotkeys
        UnregisterGlobalHotkeys();

        // Cleanup tray icon
        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        if (_settingsService.Current.LogoffOnExit && _sessionManager.TryGetChildSessionId() is uint sid)
        {
            _sessionManager.LogoffChildSession(sid);
        }
        _logger.LogInformation("MainForm closing");
    }

    // ---------------------------------------------------------------- global hotkeys

    private void RegisterGlobalHotkeys()
    {
        if (!_settingsService.Current.EnableGlobalHotkey)
        {
            _logger.LogInformation("Global hotkeys disabled by settings");
            return;
        }
        try
        {
            // Ctrl+Shift+D: Toggle connect/disconnect
            if (!RegisterHotKey(Handle, HOTKEY_TOGGLE_CONNECT, MOD_CONTROL | MOD_SHIFT, VK_D))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning("Failed to register hotkey Ctrl+Shift+D (Win32 error {Error}); may be in use", err);
                _statusLabel.Text = "全局热键 Ctrl+Shift+D 注册失败（占用中？）";
            }

            // Ctrl+Space: Show/restore window
            if (!RegisterHotKey(Handle, HOTKEY_SHOW_WINDOW, MOD_CONTROL | MOD_ALT, VK_SPACE))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning("Failed to register hotkey Ctrl+Alt+Space (Win32 error {Error}); may be in use", err);
                _statusLabel.Text = "全局热键 Ctrl+Alt+Space 注册失败（占用中？）";
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

    // ---------------------------------------------------------------- tray icon methods

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

    // ---------------------------------------------------------------- actions

    private async Task ConnectAsync()
    {
        try
        {
            _statusLabel.Text = "正在准备连接...";

            var settings = _settingsService.Current;

            // IMPORTANT: for child-session mode, RDP 3389 TCP listening is NOT
            // required — the ConnectToChildSession path connects via the local
            // session broker, not over TCP. After disabling RDP Wrapper, the
            // listener on 3389 may not exist at all on Windows Home (the native
            // termsrv.dll doesn't run an RDP server). BetterGI connects fine in
            // this state (it also uses ConnectToChildSession, which doesn't need
            // the TCP listener). So only enforce the TCP-probe pre-check for
            // standard RDP mode. The probe runs on a worker thread so the UI
            // does not freeze for up to ~10s on a cold listener.
            if (settings.ConnectionMode != ConnectionMode.ChildSession
                && !await _sessionManager.IsRdpListenerActiveAsync())
            {
                MessageBox.Show(
                    "RDP 监听器暂时未响应连接探测。可能原因：\n" +
                    "1) TermService 正在重启（禁用 RDP Wrapper 后需要 10–20 秒）— 请稍等再试\n" +
                    "2) 真未监听 — 运行「环境检查/修复」→「一键修复」\n" +
                    "3) 防火墙/杀软拦截 127.0.0.1:3389",
                    "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _statusLabel.Text = "RDP 未就绪";
                return;
            }

            // Create the RDP host in the viewer panel
            _viewerPanel.Controls.Clear();
            _rdpHost = new RdpActiveXHost(_loggerFactory.CreateLogger<RdpActiveXHost>());
            _rdpHost.LoginCompleted += OnRdpLoginComplete;
            _rdpHost.ConnectionFailed += OnRdpConnectionFailed;
            _rdpHost.RequestedGoFullScreen += OnRdpRequestFullScreen;
            _rdpHost.RequestedLeaveFullScreen += OnRdpRequestLeaveFullScreen;
            _viewerPanel.Controls.Add(_rdpHost);

            // CRITICAL: Force the AxHost to be fully created AND OLE in-place
            // activated BEFORE calling Connect. In WinForms, Controls.Add creates
            // the handle synchronously, but the ActiveX control's OLE in-place
            // activation (DoVerb(OLEIVERB_INPLACEACTIVATE)) happens during
            // WM_SHOWWINDOW / sizing — which the message pump hasn't processed
            // yet at this point.
            //
            // BetterGI (WPF + WindowsFormsHost) does not have this problem because
            // the WPF layout/render pass completes before ConnectToChildSession is
            // called — the child-session broker rejects requests from an ActiveX
            // that hasn't completed OLE activation (returns errConnectToServer /
            // disconnect reason 516).
            //
            // CreateControl() forces the full create + size + show sequence
            // synchronously, including OLE in-place activation.
            _rdpHost.CreateControl();

            var port = settings.RdpPort is > 0 and not 3389
                ? settings.RdpPort
                : _sessionManager.GetConfiguredRdpPort();

            _statusLabel.Text = $"正在连接 127.0.0.1:{port} ...";

            // Defer the Connect call to the next message-pump iteration so the
            // control's first paint cycle (and any remaining OLE activation
            // messages) are fully processed before mstscax starts the connection.
            // The Double-BeginInvoke gives WinForms one extra idle to complete
            // in-place activation for the newly-created ActiveX host.
            BeginInvoke(new Action(() => BeginInvoke(new Action(() =>
            {
                if (_rdpHost is null || _rdpHost.IsDisposed) return;

                // RDPI Wrapper (TermWrap.dll) hook detection. BetterGI's docs
                // explicitly state that RDP Wrapper and the child-session feature
                // are mutually exclusive — the TermWrap hook prevents the
                // child-session broker from creating sessions even when
                // WTSIsChildSessionsEnabled returns true. Surface a clear warning
                // so the user knows what to do instead of showing the cryptic
                // "cannot connect to remote computer" (516) error.
                if (settings.ConnectionMode == ConnectionMode.ChildSession)
                {
                    var hookCheck = _environmentVerifier.CheckRdpWrapperHook();
                    if (!hookCheck.Pass)
                    {
                        _logger.LogWarning(
                            "RDP Wrapper hook detected; child session will fail: {Detail}",
                            hookCheck.Detail);
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

                // CRITICAL for child sessions: BetterGI's ChildSessionService.StartAsync
                // always calls WTSEnableChildSessions(true) via EnsureChildSessionsEnabled
                // right before ConnectCore — even though WTSIsChildSessionsEnabled would
                // already return true. This "re-activates" the child-session feature in the
                // CURRENT process/session context. Without this priming, the child-session
                // broker rejects the ConnectToChildSession request with errConnectToServer
                // (516) and WTSGetChildSessionId returns ERROR_NOT_FOUND (1168).
                //
                // AkiSpace previously only called EnableChildSessions in the elevated
                // "one-click fix" flow — the *connecting* process never re-enabled it, so
                // the broker had no child-session registered for this session.
                if (settings.ConnectionMode == ConnectionMode.ChildSession)
                {
                    var enableResult = _sessionManager.EnableChildSessions();
                    _logger.LogInformation(
                        "WTSEnableChildSessions(true) before connect returned {Result}",
                        enableResult);
                }

                // Use different connection mode based on settings
                if (settings.ConnectionMode == ConnectionMode.ChildSession)
                {
                    // BetterGI style: connect to child session (same user)
                    _rdpHost.ConnectToChildSession(
                        settings.DesktopWidth, settings.DesktopHeight, settings.ColorDepth,
                        port, settings.SmartSizing,
                        settings.SendSystemShortcutsToRemote, settings.AudioRedirected);
                }
                else
                {
                    // Standard RDP: connect with different user account
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
            // Only enable GameMouse in non-child-session mode (the custom mouse
            // forwarder requires a paired replay agent in the child session,
            // which is not yet implemented; the child-session path does not need
            // it because the RDP control's own absolute cursor suffices).
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
            _statusLabel.Text = "连接失败";
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
        _btnGameMouse.Text = "游戏鼠标:关";
        _statusLabel.Text = "已断开";
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
            _btnGameMouse.Text = "游戏鼠标:关";
            _statusLabel.Text = "子会话已终止";
        }
        else
        {
            _statusLabel.Text = "终止失败";
        }
    }

    private void ToggleGameMouse()
    {
        var enabled = _mouseForwarder.IsGameMouseModeEnabled;
        _mouseForwarder.SetGameMouseModeEnabled(!enabled);
        _btnGameMouse.Text = enabled ? "游戏鼠标:关" : "游戏鼠标:开";
        _statusLabel.Text = enabled ? "游戏鼠标已关闭" : "游戏鼠标已开启（在分身窗口内移动鼠标以转发）";
        _settingsService.Update(s => s.GameMouseModeEnabled = !enabled);
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
            _statusLabel.Text = $"已在分身（会话 {sid.Value}）中启动 {Path.GetFileName(ofd.FileName)}";
        }
        else
        {
            _statusLabel.Text = "启动失败（详见日志）";
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
            _statusLabel.Text = "设置已保存";
        }
    }

    // ---------------------------------------------------------------- RDP events

    private void OnRdpLoginComplete()
    {
        if (_closing) return;
        _logger.LogInformation("RDP login complete event");
        _statusLabel.Text = "已连接 — 分身桌面就绪";
        _lblConnection.Text = "连接: 已连接";
        _btnGameMouse.Enabled = true;
        _isConnected = true;
        UpdateTrayIcon(true);

        // Focus the RDP input window so keyboard goes to the child session
        BeginInvoke(() => _rdpHost?.TryFocusRdpInputWindow());
    }

    private void OnRdpConnectionFailed(string reason)
    {
        if (_closing) return;
        _logger.LogWarning("RDP connection failed: {Reason}", reason);
        _statusLabel.Text = "连接失败";
        _lblConnection.Text = "连接: 失败";
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
        BeginInvoke(() =>
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
        });
    }

    // ---------------------------------------------------------------- status refresh

    private void RefreshStatus()
    {
        if (_closing || !IsHandleCreated) return;

        try
        {
            // Child session
            var sid = _sessionManager.TryGetChildSessionId();
            _lblChildSession.Text = sid.HasValue
                ? $"子会话: 活动 (ID {sid.Value})"
                : "子会话: 无";

            // RDP wrapper
            _lblWrapper.Text = _sessionManager.IsRdpWrapperInstalled()
                ? "RDP 解锁: 已安装"
                : "RDP 解锁: 未安装";

            // Connection
            if (_rdpHost != null)
            {
                var connected = _rdpHost.GetConnectedState();
                _lblConnection.Text = connected ? "连接: 已连接" : "连接: 未连接";
                _isConnected = connected;
                UpdateTrayIcon(connected);
            }

            // Performance monitoring
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

    // ---------------------------------------------------------------- forwarder callbacks

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
}