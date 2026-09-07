using AkiSpace.Common;
using AkiSpace.Services;

namespace AkiSpace.Forms;

/// <summary>Settings dialog bound to the AppSettings model.</summary>
public sealed class SettingsDialog : Form
{
    private readonly SettingsService _settingsService;
    private readonly NumericUpDown _numWidth = new();
    private readonly NumericUpDown _numHeight = new();
    private readonly NumericUpDown _numColorDepth = new();
    private readonly NumericUpDown _numPort = new();
    private readonly ComboBox _cmbConnectionMode = new();
    private readonly CheckBox _chkSmartSizing = new();
    private readonly CheckBox _chkShortcutsRemote = new();
    private readonly CheckBox _chkAudioRedirect = new();
    private readonly CheckBox _chkAutoConnect = new();
    private readonly CheckBox _chkLogoffOnExit = new();
    private readonly CheckBox _chkMinimizeToTray = new();
    private readonly CheckBox _chkShowPerformance = new();
    private readonly CheckBox _chkEnableGlobalHotkey = new();
    private readonly TextBox _txtCloneUsername = new();
    private readonly TextBox _txtClonePassword = new();
    private readonly TextBox _txtLaunchProgramPath = new();
    private readonly Button _btnBrowseLaunchProgram = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    public SettingsDialog(SettingsService settingsService)
    {
        _settingsService = settingsService;
        BuildUi();
        LoadSettings();
    }

    private void BuildUi()
    {
        Text = "设置";
        Size = new Size(520, 620);
        MinimumSize = new Size(520, 620);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 520,
            ColumnCount = 2,
            RowCount = 16,
            Padding = new Padding(20, 16, 20, 8),
            BackColor = Theme.Bg,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(int row, string label, Control control)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.Text,
                BackColor = Theme.Bg,
            };
            grid.Controls.Add(lbl, 0, row);
            grid.Controls.Add(control, 1, row);
        }

        // Style controls
        StyleControl(_numWidth);
        StyleControl(_numHeight);
        StyleControl(_numColorDepth);
        StyleControl(_numPort);
        StyleComboBox(_cmbConnectionMode);
        StyleCheckbox(_chkSmartSizing);
        StyleCheckbox(_chkShortcutsRemote);
        StyleCheckbox(_chkAudioRedirect);
        StyleCheckbox(_chkAutoConnect);
        StyleCheckbox(_chkLogoffOnExit);
        StyleCheckbox(_chkMinimizeToTray);
        StyleCheckbox(_chkShowPerformance);
        StyleCheckbox(_chkEnableGlobalHotkey);
        StyleTextBox(_txtCloneUsername);
        StyleTextBox(_txtClonePassword);
        StyleTextBox(_txtLaunchProgramPath);
        _btnBrowseLaunchProgram.Text = "浏览...";
        _btnBrowseLaunchProgram.Size = new Size(60, 24);
        _btnBrowseLaunchProgram.FlatStyle = FlatStyle.Flat;
        _btnBrowseLaunchProgram.BackColor = Theme.Control;
        _btnBrowseLaunchProgram.ForeColor = Theme.Text;
        _btnBrowseLaunchProgram.FlatAppearance.BorderColor = Theme.Border;
        _btnBrowseLaunchProgram.Cursor = Cursors.Hand;
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

        var launchProgramRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = Theme.Bg,
        };
        launchProgramRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        launchProgramRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        launchProgramRow.Controls.Add(_txtLaunchProgramPath, 0, 0);
        launchProgramRow.Controls.Add(_btnBrowseLaunchProgram, 1, 0);

        _numWidth.Minimum = 800; _numWidth.Maximum = 7680; _numWidth.Increment = 160;
        _numHeight.Minimum = 600; _numHeight.Maximum = 4320; _numHeight.Increment = 120;
        _numColorDepth.Minimum = 8; _numColorDepth.Maximum = 32; _numColorDepth.Increment = 8;
        _numPort.Minimum = 1; _numPort.Maximum = 65535;

        _cmbConnectionMode.Items.AddRange(new object[] { "标准RDP（不同用户）", "子会话（同一用户）" });
        _cmbConnectionMode.DropDownStyle = ComboBoxStyle.DropDownList;

        AddRow(0, "连接模式", _cmbConnectionMode);
        AddRow(1, "分身桌面宽度", _numWidth);
        AddRow(2, "分身桌面高度", _numHeight);
        AddRow(3, "颜色深度 (位)", _numColorDepth);
        AddRow(4, "RDP 端口", _numPort);
        AddRow(5, "缩放适应窗口", _chkSmartSizing);
        AddRow(6, "快捷键发送到分身", _chkShortcutsRemote);
        AddRow(7, "音频重定向", _chkAudioRedirect);
        AddRow(8, "启动时自动连接", _chkAutoConnect);
        AddRow(9, "退出时终止子会话", _chkLogoffOnExit);
        AddRow(10, "最小化到托盘", _chkMinimizeToTray);
        AddRow(11, "显示性能监控", _chkShowPerformance);
        AddRow(12, "全局热键 Ctrl+Shift+D", _chkEnableGlobalHotkey);
        AddRow(13, "分身账户用户名", _txtCloneUsername);
        AddRow(14, "分身账户密码", _txtClonePassword);
        AddRow(15, "连接后自动启动", launchProgramRow);

        _txtClonePassword.UseSystemPasswordChar = true;

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8),
            BackColor = Theme.Surface,
        };
        _btnOk.Text = "保存";
        _btnOk.Size = new Size(100, 36);
        _btnOk.FlatStyle = FlatStyle.Flat;
        _btnOk.BackColor = Theme.AccentBlue;
        _btnOk.ForeColor = Color.White;
        _btnOk.FlatAppearance.BorderSize = 0;
        _btnOk.DialogResult = DialogResult.OK;
        _btnCancel.Text = "取消";
        _btnCancel.Size = new Size(90, 36);
        _btnCancel.FlatStyle = FlatStyle.Flat;
        _btnCancel.BackColor = Theme.Control;
        _btnCancel.ForeColor = Theme.Text;
        _btnCancel.FlatAppearance.BorderColor = Theme.Border;
        _btnCancel.DialogResult = DialogResult.Cancel;
        btnRow.Controls.Add(_btnCancel);
        btnRow.Controls.Add(_btnOk);

        Controls.Add(grid);
        Controls.Add(btnRow);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;
    }

    private static void StyleControl(NumericUpDown ctrl)
    {
        ctrl.BackColor = Theme.Control;
        ctrl.ForeColor = Theme.Text;
        ctrl.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void StyleCheckbox(CheckBox ctrl)
    {
        ctrl.BackColor = Theme.Bg;
        ctrl.ForeColor = Theme.Text;
    }

    private static void StyleTextBox(TextBox ctrl)
    {
        ctrl.BackColor = Theme.Control;
        ctrl.ForeColor = Theme.Text;
        ctrl.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void StyleComboBox(ComboBox ctrl)
    {
        ctrl.BackColor = Theme.Control;
        ctrl.ForeColor = Theme.Text;
        ctrl.FlatStyle = FlatStyle.Flat;
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
                s.CloneUsername = _txtCloneUsername.Text;
                s.ClonePassword = _txtClonePassword.Text;
                s.LaunchProgramPath = string.IsNullOrWhiteSpace(_txtLaunchProgramPath.Text)
                    ? null
                    : _txtLaunchProgramPath.Text;
            });
        }
        base.OnFormClosing(e);
    }
}