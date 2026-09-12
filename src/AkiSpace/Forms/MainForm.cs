using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using AkiSpace.Common;
using AkiSpace.Controls;
using AkiSpace.Input;
using AkiSpace.Ipc;
using AkiSpace.Native;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Forms;

/// <summary>
/// AkiSpace main window — dark theme, standard WinForms controls.
/// </summary>
public sealed class MainForm : Form
{
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
    private byte[]? _agentNonce;

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

    // System tray
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;

    private RdpActiveXHost? _rdpHost;
    private System.Windows.Forms.Timer? _statusTimer;
    private bool _closing;
    private bool _isConnected;

    private string _cloneUsername => _settingsService.Current.CloneUsername;
    private string _clonePassword => _settingsService.Current.ClonePassword;

    // Dark theme colors
    private static readonly Color BgDark = Color.FromArgb(24, 24, 27);
    private static readonly Color SurfaceDark = Color.FromArgb(38, 38, 42);
    private static readonly Color AccentDark = Color.FromArgb(99, 102, 241);
    private static readonly Color TextDark = Color.FromArgb(228, 228, 231);
    private static readonly Color TextSubtleDark = Color.FromArgb(161, 161, 170);

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
        _btnConnect.Click += async (_, _) => await ConnectAsync();

        _btnDisconnect.Text = "断开";
        _btnDisconnect.Size = new Size(90, 36);
        _btnDisconnect.FlatStyle = FlatStyle.Flat;
        _btnDisconnect.BackColor = SurfaceDark;
        _btnDisconnect.ForeColor = TextDark;
        _btnDisconnect.Cursor = Cursors.Hand;
        _btnDisconnect.Enabled = false;
        _btnDisconnect.Click += (_, _) => Disconnect();

        _btnTerminate.Text = "终止";
        _btnTerminate.Size = new Size(90, 36);
        _btnTerminate.FlatStyle = FlatStyle.Flat;
        _btnTerminate.BackColor = SurfaceDark;
        _btnTerminate.ForeColor = TextDark;
        _btnTerminate.Cursor = Cursors.Hand;
        _btnTerminate.Enabled = false;
        _btnTerminate.Click += (_, _) => TerminateChildSession();

        _btnGameMouse.Text = "游戏鼠标";
        _btnGameMouse.Size = new Size(100, 36);
        _btnGameMouse.FlatStyle = FlatStyle.Flat;
        _btnGameMouse.BackColor = SurfaceDark;
        _btnGameMouse.ForeColor = TextDark;
        _btnGameMouse.Cursor = Cursors.Hand;
        _btnGameMouse.Enabled = false;
        _btnGameMouse.Visible = false;
        _btnGameMouse.Click += (_, _) => ToggleGameMouse();

        _btnLaunch.Text = "启动程序";
        _btnLaunch.Size = new Size(100, 36);
        _btnLaunch.FlatStyle = FlatStyle.Flat;
        _btnLaunch.BackColor = SurfaceDark;
        _btnLaunch.ForeColor = TextDark;
        _btnLaunch.Cursor = Cursors.Hand;
        _btnLaunch.Enabled = false;
        _btnLaunch.Click += (_, _) => LaunchProgramInChildSession();

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
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        // Resolve the clone-account pipe DACL grant BEFORE restoring game-mouse mode:
        // SetGameMouseModeEnabled(true) starts the pipe listener, and CreateServerStream
        // bakes the pipe DACL at stream-creation time. The clone-agent launch
        // (OnRdpLoginComplete → LaunchAgentInChildSession) happens LATER, so without this
        // pre-set, a persisted GameMouseModeEnabled=true would start the very first
        // listening stream with a primary+SYSTEM-only DACL and the cross-user clone agent
        // would be denied connect at the pipe level. (The nonce needs no such ordering —
        // VerifyHandshakeAsync reads _expectedNonce per connection, not at stream create.)
        _pipeServer.SetAllowedClientSid(TryResolveCloneSid());

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

        if (_settingsService.Current.LogoffOnExit && _sessionManager.TryGetChildSessionId() is uint sid)
        {
            _sessionManager.LogoffChildSession(sid);
        }
        _logger.LogInformation("MainForm closing");
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

    private void LaunchAgentInChildSession()
    {
        try
        {
            _agentNonce = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(_agentNonce);

            _pipeServer.SetNonce(_agentNonce);

            // Standard-RDP mode: the agent runs under a DIFFERENT account, so the pipe DACL
            // must also admit the clone account SID (else its connect is denied before the
            // handshake). Null for child-session mode (same user) → server keeps the tight
            // primary+SYSTEM-only DACL.
            _pipeServer.SetAllowedClientSid(TryResolveCloneSid());

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

            // Deliver the nonce out-of-band via a DACL-protected file instead of the command
            // line — argv leaks via the Task Scheduler task XML under %WINDIR%\System32\Tasks
            // and the child process PEB.
            var nonceFilePath = WriteNonceToProtectedTempFile(_agentNonce);
            if (nonceFilePath is null)
            {
                _logger.LogWarning("Failed to write nonce file; agent not launched");
                return;
            }

            var args = $"--agent --nonce-file \"{nonceFilePath}\"";
            if (_processLauncher.LaunchInChildSession(exePath, sid.Value, args))
            {
                _logger.LogInformation("Agent launched in child session {Sid} (nonce delivered via file)", sid.Value);
                _lblConnection.Text = "连接: 已连接 — Agent 启动中";
            }
            else
            {
                _logger.LogWarning("Agent launch failed in child session {Sid}", sid.Value);
                // Launch failed — the agent will never consume the nonce, so delete it here
                // to avoid orphaning a (DACL-restricted) nonce file on disk.
                try { File.Delete(nonceFilePath); } catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch agent in child session");
        }
    }

    /// <summary>
    /// Writes the handshake nonce to a uniquely-named file whose DACL grants Read to the
    /// current identity + SYSTEM + (for standard-RDP mode) the clone account, so the
    /// separately-launched agent — which may run under a DIFFERENT user — can read it, while
    /// unrelated local users cannot. The nonce is NEVER placed on the command line (argv leaks
    /// into the Task Scheduler task XML and the PEB). The agent reads+deletes it via
    /// <see cref="PipeClient.ReadNonceFromFile"/>.
    ///
    /// The file lives under %PROGRAMDATA%\AkiSpace because that tree grants the Users group
    /// traverse+read by inheritance, letting the clone account (a Users member) reach the
    /// file; %TEMP% would be per-user and unreadable cross-account.
    /// </summary>
    private string? WriteNonceToProtectedTempFile(byte[] nonce)
    {
        var path = Path.Combine(NonceDirectory(), $"nonce_{Guid.NewGuid():N}.bin");
        try
        {
            // Build the restricted DACL FIRST and create the file WITH it in one atomic
            // step (FileStream's FileSecurity overload → CreateFile with SECURITY_ATTRIBUTES).
            // This avoids the write-then-tighten TOCTOU window: the file never exists with
            // content under %PROGRAMDATA%'s inherited Users:Read ACE. Owner gets Write so it
            // can write through the handle, Delete so the launch-failure path can remove it.
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var identity = WindowsIdentity.GetCurrent();
            security.AddAccessRule(new FileSystemAccessRule(
                identity.User!, // non-null for an interactive logon token
                FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.Read, InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Allow));
            // Clone account (standard-RDP / cross-user mode) — Read so the agent running as
            // AkiSpaceUser can consume the nonce, + Delete so it can remove the file after
            // reading (the owner's delete right does not transfer to a different identity).
            // No-op for same-user child-session mode.
            foreach (var sid in ResolveNonceFileSids())
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid, FileSystemRights.Read | FileSystemRights.Delete, InheritanceFlags.None,
                    PropagationFlags.None, AccessControlType.Allow));
            }

            // .NET 8 dropped the FileStream(FileSecurity) ctor, so we close the TOCTOU window
            // like this: open with FileShare.None (an exclusive handle — no other process can
            // open the file at all while we hold it), tighten the DACL via
            // FileStream.SetAccessControl ON THE ALREADY-OPEN HANDLE (FileInfo.SetAccessControl
            // would try a second path-open and hit a sharing violation against our exclusive
            // handle), and only THEN write the nonce bytes. The handle's WriteData access is
            // fixed at open time and survives the ACL change; the owner has implicit WRITE_DAC
            // to set it. Net effect: bytes that exist on disk are only ever present under the
            // restricted DACL, and during the pre-tighten window the file is empty AND locked.
            using (var fs = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
            {
                fs.SetAccessControl(security);
                fs.Write(nonce, 0, nonce.Length);
                fs.Flush(flushToDisk: true);
            }
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to secure nonce file {Path}", path);
            try { File.Delete(path); } catch { /* best-effort */ }
            return null;
        }
    }

    /// <summary>%PROGRAMDATA%\AkiSpace, created if absent. Traversable+readable by the Users group by inheritance.</summary>
    private static string NonceDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AkiSpace");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Extra SIDs the nonce file must be readable by — currently the configured clone account
    /// (standard-RDP mode). Returns empty for child-session mode (same user) or when the
    /// account cannot be translated to a SID.
    /// </summary>
    private IEnumerable<SecurityIdentifier> ResolveNonceFileSids()
    {
        var sid = TryResolveCloneSid();
        if (sid is not null) yield return sid;
    }

    /// <summary>Resolves the configured clone account to a SID, or null (same-user/unresolvable).</summary>
    private SecurityIdentifier? TryResolveCloneSid()
    {
        try
        {
            if (_settingsService.Current.ConnectionMode == ConnectionMode.ChildSession)
                return null; // agent runs as the current user — no extra grant needed

            var user = _settingsService.Current.CloneUsername;
            if (string.IsNullOrWhiteSpace(user)) return null;

            // Try the qualified form first (MACHINE\user for plain names, or the
            // DOMAIN\user / UPN exactly as given), then fall back to an unqualified
            // lookup which succeeds for domain-joined / MSA identities the local
            // SAM cannot resolve. Fail-closed (null + warning) if neither works.
            var candidates = user.Contains('\\') || user.Contains('@')
                ? new[] { new NTAccount(user) }
                : new[] { new NTAccount(Environment.MachineName, user), new NTAccount(user) };

            foreach (var account in candidates)
            {
                try
                {
                    if (account.Translate(typeof(SecurityIdentifier)) is SecurityIdentifier sid)
                        return sid;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SID lookup failed for account form '{Account}'", account.Value);
                }
            }
            // Every candidate form failed to translate — surface it at Warning level so the
            // fail-closed degradation is never silent (per-candidate Debug logs alone are not
            // enough to diagnose a broken agent connection later).
            _logger.LogWarning("Could not resolve clone account '{User}' to a SID; agent may fail to read the nonce file in standard-RDP mode", _cloneUsername);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve clone account '{User}' to a SID; agent may fail to read the nonce file in standard-RDP mode", _cloneUsername);
            return null;
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
}
