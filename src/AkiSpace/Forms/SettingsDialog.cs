using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AkiSpace.Common;
using AkiSpace.Controls.Styled;
using AkiSpace.Services;

namespace AkiSpace.Forms;

/// <summary>
/// Settings dialog - Ethereal Glass design with card sections and styled controls.
/// </summary>
public sealed class SettingsDialog : Form
{
    private readonly SettingsService _settingsService;

    // Connection section
    private readonly StyledComboBox _cmbConnectionMode = new();
    private readonly StyledNumericUpDown _numWidth = new();
    private readonly StyledNumericUpDown _numHeight = new();
    private readonly StyledNumericUpDown _numColorDepth = new();
    private readonly StyledNumericUpDown _numPort = new();
    private readonly StyledCheckBox _chkSmartSizing = new();
    private readonly StyledCheckBox _chkShortcutsRemote = new();
    private readonly StyledCheckBox _chkAudioRedirect = new();

    // Behavior section
    private readonly StyledCheckBox _chkAutoConnect = new();
    private readonly StyledCheckBox _chkLogoffOnExit = new();
    private readonly StyledCheckBox _chkMinimizeToTray = new();
    private readonly StyledCheckBox _chkShowPerformance = new();
    private readonly StyledCheckBox _chkEnableGlobalHotkey = new();
    private readonly StyledCheckBox _chkGameMouseMode = new();

    // Account section
    private readonly StyledTextBox _txtCloneUsername = new();
    private readonly StyledTextBox _txtClonePassword = new();

    // Auto-launch section
    private readonly StyledTextBox _txtLaunchProgramPath = new();
    private readonly GhostButton _btnBrowseLaunchProgram = new();

    // Buttons
    private readonly PrimaryButton _btnOk = new();
    private readonly GhostButton _btnCancel = new();

    public SettingsDialog(SettingsService settingsService)
    {
        _settingsService = settingsService;
        BuildUi();
        LoadSettings();
    }

    private void BuildUi()
    {
        Text = "设置";
        Size = new Size(580, 700);
        MinimumSize = new Size(580, 700);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = ThemeManager.Current.BgBase;
        ForeColor = ThemeManager.Current.TextPrimary;
        Font = ThemeManager.Current.GetFontSans(13f);

        var mainPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(ThemeTokens.Space.S6), // 24px
            AutoScroll = true,
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0),
        };

        // --- Connection Card ---
        var connCard = new DoubleBezelCard
        {
            Title = "连接配置",
            Subtitle = "分身桌面的连接参数与模式",
            Width = 500,
        };
        BuildConnectionCard(connCard);
        flow.Controls.Add(connCard);

        // --- Behavior Card ---
        var behaviorCard = new DoubleBezelCard
        {
            Title = "行为设置",
            Subtitle = "启动、托盘、热键与性能监控",
            Width = 500,
        };
        BuildBehaviorCard(behaviorCard);
        flow.Controls.Add(behaviorCard);

        // --- Account Card ---
        var accountCard = new DoubleBezelCard
        {
            Title = "分身账户",
            Subtitle = "标准 RDP 模式下的登录凭据",
            Width = 500,
        };
        BuildAccountCard(accountCard);
        flow.Controls.Add(accountCard);

        // --- Auto-launch Card ---
        var launchCard = new DoubleBezelCard
        {
            Title = "自动启动",
            Subtitle = "连接成功后在分身中启动程序",
            Width = 500,
        };
        BuildLaunchCard(launchCard);
        flow.Controls.Add(launchCard);

        mainPanel.Controls.Add(flow);
        Controls.Add(mainPanel);

        // Button bar (fixed at bottom)
        var btnPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 70,
            Padding = new Padding(ThemeTokens.Space.S6, ThemeTokens.Space.S4, ThemeTokens.Space.S6, ThemeTokens.Space.S4),
            BackColor = Color.Transparent,
        };
        var btnFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
        };

        _btnOk.Text = "保存";
        _btnOk.Size = new Size(100, 40);
        _btnOk.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        _btnCancel.Text = "取消";
        _btnCancel.Size = new Size(90, 40);
        _btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        btnFlow.Controls.Add(_btnCancel);
        btnFlow.Controls.Add(_btnOk);
        btnPanel.Controls.Add(btnFlow);
        Controls.Add(btnPanel);

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        // Theme change handling
        ThemeManager.Current.ThemeChanged += (_, _) => OnThemeChanged();
    }

    private void BuildConnectionCard(DoubleBezelCard card)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, ThemeTokens.Space.S2, 0, 0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _cmbConnectionMode.Items.AddRange(new object[] { "标准RDP（不同用户）", "子会话（同一用户）" });
        _cmbConnectionMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbConnectionMode.Width = 300;

        _numWidth.Minimum = 800; _numWidth.Maximum = 7680; _numWidth.Increment = 160; _numWidth.Width = 150;
        _numHeight.Minimum = 600; _numHeight.Maximum = 4320; _numHeight.Increment = 120; _numHeight.Width = 150;
        _numColorDepth.Minimum = 8; _numColorDepth.Maximum = 32; _numColorDepth.Increment = 8; _numColorDepth.Width = 100;
        _numPort.Minimum = 1; _numPort.Maximum = 65535; _numPort.Width = 150;

        _chkSmartSizing.Text = "缩放适应窗口 (Smart Sizing)";
        _chkShortcutsRemote.Text = "系统快捷键发送到分身";
        _chkAudioRedirect.Text = "音频重定向到本机";

        AddRow(grid, 0, "连接模式", _cmbConnectionMode);
        AddRow(grid, 1, "分身桌面宽度", _numWidth, "像素，建议匹配显示器分辨率");
        AddRow(grid, 2, "分身桌面高度", _numHeight, "像素，建议匹配显示器分辨率");
        AddRow(grid, 3, "颜色深度", _numColorDepth, "位，32位为真彩色");
        AddRow(grid, 4, "RDP 端口", _numPort, "默认 3389，仅当你在系统层面改过 RDP 监听端口时同步修改");
        AddRow(grid, 5, "", _chkSmartSizing);
        AddRow(grid, 6, "", _chkShortcutsRemote);
        // Add audio redirect in next row
        var row7 = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Height = 36 };
        _chkAudioRedirect.Dock = DockStyle.Fill;
        row7.Controls.Add(_chkAudioRedirect);
        grid.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill, BackColor = Color.Transparent }, 0, 7);
        grid.Controls.Add(row7, 1, 7);

        card.ContentControls.Add(grid);
    }

    private void BuildBehaviorCard(DoubleBezelCard card)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, ThemeTokens.Space.S2, 0, 0),
        };

        _chkAutoConnect.Text = "启动时自动连接分身";
        _chkLogoffOnExit.Text = "退出时终止子会话";
        _chkMinimizeToTray.Text = "最小化到系统托盘";
        _chkShowPerformance.Text = "状态栏显示性能监控 (CPU/内存)";
        _chkEnableGlobalHotkey.Text = "启用全局热键 (Ctrl+Shift+D 切换连接, Ctrl+Alt+Space 显示窗口)";
        _chkGameMouseMode.Text = "启用游戏鼠标模式（仅标准 RDP，需回放 Agent）";

        var checks = new[] { _chkAutoConnect, _chkLogoffOnExit, _chkMinimizeToTray, _chkShowPerformance, _chkEnableGlobalHotkey, _chkGameMouseMode };
        for (int i = 0; i < checks.Length; i++)
        {
            checks[i].Dock = DockStyle.Top;
            checks[i].Margin = new Padding(0, 0, 0, 8);
            checks[i].Font = ThemeManager.Current.GetFontSans(13f);
            grid.Controls.Add(checks[i], 0, i);
        }

        card.ContentControls.Add(grid);
    }

    private void BuildAccountCard(DoubleBezelCard card)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, ThemeTokens.Space.S2, 0, 0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _txtCloneUsername.Width = 300;
        _txtClonePassword.Width = 300;
        _txtClonePassword.UseSystemPasswordChar = true;

        AddRow(grid, 0, "分身账户用户名", _txtCloneUsername, "标准 RDP 模式必填，如 AkiSpaceUser");
        AddRow(grid, 1, "分身账户密码", _txtClonePassword, "密码使用 DPAPI 加密存储，默认 lb33 为占位符");

        card.ContentControls.Add(grid);
    }

    private void BuildLaunchCard(DoubleBezelCard card)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, ThemeTokens.Space.S2, 0, 0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        _txtLaunchProgramPath.Dock = DockStyle.Fill;
        _txtLaunchProgramPath.Width = 350;

        _btnBrowseLaunchProgram.Text = "浏览...";
        _btnBrowseLaunchProgram.Size = new Size(90, 40);
        _btnBrowseLaunchProgram.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "选择连接后自动启动的程序",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                _txtLaunchProgramPath.Text = ofd.FileName;
        };

        var rowPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Height = 44 };
        var rowFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
        rowFlow.Controls.Add(_txtLaunchProgramPath);
        rowFlow.Controls.Add(_btnBrowseLaunchProgram);
        rowPanel.Controls.Add(rowFlow);

        var lbl = new Label
        {
            Text = "连接后自动启动",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ThemeManager.Current.TextPrimary,
            BackColor = Color.Transparent,
            Font = ThemeManager.Current.GetFontSans(13f),
            Margin = new Padding(0, 0, ThemeTokens.Space.S4, 0),
            AutoSize = true,
        };
        grid.Controls.Add(lbl, 0, 0);
        grid.Controls.Add(rowPanel, 1, 0);

        card.ContentControls.Add(grid);
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control control, string? hint = null)
    {
        var lbl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ThemeManager.Current.TextPrimary,
            BackColor = Color.Transparent,
            Font = ThemeManager.Current.GetFontSans(13f),
            Margin = new Padding(0, 0, ThemeTokens.Space.S4, 0),
            AutoSize = true,
        };
        control.Margin = new Padding(0, 4, 0, 8);
        control.Dock = DockStyle.Fill;
        grid.Controls.Add(lbl, 0, row);
        grid.Controls.Add(control, 1, row);

        if (!string.IsNullOrEmpty(hint))
        {
            var hintLbl = new Label
            {
                Text = hint,
                Dock = DockStyle.Fill,
                ForeColor = ThemeManager.Current.TextTertiary,
                BackColor = Color.Transparent,
                Font = ThemeManager.Current.GetFontSans(11f),
                AutoSize = true,
                Margin = new Padding(0, -4, 0, 8),
            };
            grid.SetColumnSpan(hintLbl, 2);
            grid.Controls.Add(hintLbl, 0, row + 1);
        }
    }

    private void LoadSettings()
    {
        var s = _settingsService.Current;
        _cmbConnectionMode.SelectedIndex = s.ConnectionMode == ConnectionMode.ChildSession ? 1 : 0;
        _numWidth.Value = s.DesktopWidth;
        _numHeight.Value = s.DesktopHeight;
        _numColorDepth.Value = s.ColorDepth;
        _numPort.Value = s.RdpPort;
        _chkSmartSizing.Checked = s.SmartSizing;
        _chkShortcutsRemote.Checked = s.SendSystemShortcutsToRemote;
        _chkAudioRedirect.Checked = s.AudioRedirected;
        _chkAutoConnect.Checked = s.AutoConnect;
        _chkLogoffOnExit.Checked = s.LogoffOnExit;
        _chkMinimizeToTray.Checked = s.MinimizeToTray;
        _chkShowPerformance.Checked = s.ShowPerformance;
        _chkEnableGlobalHotkey.Checked = s.EnableGlobalHotkey;
        _chkGameMouseMode.Checked = s.GameMouseModeEnabled;
        _txtCloneUsername.Text = s.CloneUsername;
        _txtClonePassword.Text = s.ClonePassword;
        _txtLaunchProgramPath.Text = s.LaunchProgramPath ?? string.Empty;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            _settingsService.Update(s =>
            {
                s.ConnectionMode = _cmbConnectionMode.SelectedIndex == 1
                    ? ConnectionMode.ChildSession
                    : ConnectionMode.StandardRdp;
                s.DesktopWidth = (int)_numWidth.Value;
                s.DesktopHeight = (int)_numHeight.Value;
                s.ColorDepth = (int)_numColorDepth.Value;
                s.RdpPort = (int)_numPort.Value;
                s.SmartSizing = _chkSmartSizing.Checked;
                s.SendSystemShortcutsToRemote = _chkShortcutsRemote.Checked;
                s.AudioRedirected = _chkAudioRedirect.Checked;
                s.AutoConnect = _chkAutoConnect.Checked;
                s.LogoffOnExit = _chkLogoffOnExit.Checked;
                s.MinimizeToTray = _chkMinimizeToTray.Checked;
                s.ShowPerformance = _chkShowPerformance.Checked;
                s.EnableGlobalHotkey = _chkEnableGlobalHotkey.Checked;
                s.GameMouseModeEnabled = _chkGameMouseMode.Checked;
                s.CloneUsername = _txtCloneUsername.Text;
                s.ClonePassword = _txtClonePassword.Text;
                s.LaunchProgramPath = string.IsNullOrWhiteSpace(_txtLaunchProgramPath.Text)
                    ? null
                    : _txtLaunchProgramPath.Text;
            });
        }
        base.OnFormClosing(e);
    }

    private void OnThemeChanged()
    {
        BackColor = ThemeManager.Current.BgBase;
        ForeColor = ThemeManager.Current.TextPrimary;
        Font = ThemeManager.Current.GetFontSans(13f);

        foreach (var ctrl in GetAllControls(this))
        {
            if (ctrl is Label lbl)
            {
                lbl.ForeColor = ThemeManager.Current.TextPrimary;
                lbl.Font = ThemeManager.Current.GetFontSans(lbl.Font.Size);
            }
        }

        Invalidate();
    }

    private static IEnumerable<Control> GetAllControls(Control root)
    {
        var stack = new Stack<Control>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            yield return c;
            foreach (Control child in c.Controls)
                stack.Push(child);
        }
    }
}