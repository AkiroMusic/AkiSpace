using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Forms;

/// <summary>
/// One-click environment check & fix dialog for the desktop-clone prerequisites:
/// RDP enabled, multi-session, RDP Wrapper (Home), StartRCM, TermService,
/// firewall loopback rule, child sessions, listener, termsrv version.
/// </summary>
public sealed class SetupDialog : Form
{
    // Dark theme colors
    private static readonly Color DarkBg = Color.FromArgb(30, 30, 30);
    private static readonly Color DarkSurface = Color.FromArgb(40, 40, 40);
    private static readonly Color DarkControl = Color.FromArgb(50, 50, 50);
    private static readonly Color DarkBorder = Color.FromArgb(70, 70, 70);
    private static readonly Color DarkText = Color.FromArgb(220, 220, 220);
    private static readonly Color DarkTextDim = Color.FromArgb(150, 150, 150);
    private static readonly Color AccentBlue = Color.FromArgb(0, 120, 215);
    private static readonly Color AccentGreen = Color.FromArgb(0, 180, 80);
    private static readonly Color AccentRed = Color.FromArgb(220, 50, 50);

    private readonly ILogger<SetupDialog> _logger;
    private readonly EnvironmentVerifier _verifier;
    private readonly ChildSessionManager _sessionManager;

    private readonly ListView _listView = new();
    private readonly Button _btnCheck = new();
    private readonly Button _btnFix = new();
    private readonly Button _btnClose = new();
    private readonly Label _lblHint = new();

    public SetupDialog(
        ILogger<SetupDialog> logger,
        EnvironmentVerifier verifier,
        ChildSessionManager sessionManager)
    {
        _logger = logger;
        _verifier = verifier;
        _sessionManager = sessionManager;

        BuildUi();
        RunChecks();
    }

    private void BuildUi()
    {
        Text = "环境检查 / 修复 — 桌面分身前置条件";
        Size = new Size(720, 560);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DarkBg;
        ForeColor = DarkText;
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);

        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.FullRowSelect = true;
        _listView.GridLines = true;
        _listView.BackColor = DarkControl;
        _listView.ForeColor = DarkText;
        _listView.BorderStyle = BorderStyle.None;
        _listView.Columns.Add("检查项", 260);
        _listView.Columns.Add("状态", 90);
        _listView.Columns.Add("详情", 340);

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 100,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = DarkSurface
        };
        _lblHint.Dock = DockStyle.Top;
        _lblHint.Height = 40;
        _lblHint.Text = "提示：家庭版 Windows 需要 RDP Wrapper 解锁多会话。\n" +
                        "「一键修复」会修改注册表并重启 TermService，需要管理员权限。";
        _lblHint.ForeColor = DarkTextDim;
        _lblHint.BackColor = DarkSurface;

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = DarkSurface
        };
        _btnCheck.Text = "重新检查";
        _btnCheck.Size = new Size(110, 36);
        _btnCheck.FlatStyle = FlatStyle.Flat;
        _btnCheck.BackColor = DarkControl;
        _btnCheck.ForeColor = DarkText;
        _btnCheck.FlatAppearance.BorderColor = DarkBorder;
        _btnCheck.Click += (_, _) => RunChecks();
        _btnFix.Text = "一键修复";
        _btnFix.Size = new Size(110, 36);
        _btnFix.FlatStyle = FlatStyle.Flat;
        _btnFix.BackColor = AccentBlue;
        _btnFix.ForeColor = Color.White;
        _btnFix.FlatAppearance.BorderSize = 0;
        _btnFix.Click += (_, _) => RunFixes();
        _btnClose.Text = "关闭";
        _btnClose.Size = new Size(90, 36);
        _btnClose.FlatStyle = FlatStyle.Flat;
        _btnClose.BackColor = DarkControl;
        _btnClose.ForeColor = DarkText;
        _btnClose.FlatAppearance.BorderColor = DarkBorder;
        _btnClose.Click += (_, _) => Close();
        btnRow.Controls.Add(_btnClose);
        btnRow.Controls.Add(_btnFix);
        btnRow.Controls.Add(_btnCheck);

        bottom.Controls.Add(_lblHint);
        bottom.Controls.Add(btnRow);

        Controls.Add(_listView);
        Controls.Add(bottom);
    }

    private void RunChecks()
    {
        _listView.BeginUpdate();
        _listView.Items.Clear();
        foreach (var check in _verifier.RunAllChecks())
        {
            var item = new ListViewItem(check.Name);
            item.SubItems.Add(check.Pass ? "✓ 通过" : "✗ 失败");
            item.SubItems.Add(check.Detail);
            item.ForeColor = check.Pass ? AccentGreen : AccentRed;
            _listView.Items.Add(item);
        }
        _listView.EndUpdate();
        _logger.LogInformation("Environment checks completed");
    }

    private void RunFixes()
    {
        var confirm = MessageBox.Show(
            "将执行以下操作（需要管理员权限，会弹出 UAC 提示）：\n" +
            "  1. 启用 RDP（fDenyTSConnections=0）\n" +
            "  2. 允许多会话（fSingleSessionPerUser=0）\n" +
            "  3. 设置 StartRCM=1（家庭版修复）\n" +
            "  4. 安全加固（TLS + 高加密 + NLA）\n" +
            "  5. 添加防火墙回环规则（RDP 仅允许 127.0.0.1）\n" +
            "  6. 重启 TermService 服务\n" +
            "  7. 启用子会话\n\n" +
            "注意：RDP Wrapper 本身（rdpwrap.dll）不会自动安装，请按 README 手动完成。\n\n" +
            "是否继续？",
            "AkiSpace 环境修复", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        // Re-launch the app elevated with --fix-env flag.
        // The elevated process shows a console with fix results, then exits.
        try
        {
            var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null)
            {
                MessageBox.Show("无法获取程序路径。", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo(exePath, "--fix-env")
            {
                Verb = "runas",          // UAC elevation
                UseShellExecute = true,  // Required for Verb
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // User clicked "No" on UAC dialog
            if (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
            {
                MessageBox.Show("已取消管理员权限，修复未执行。", "AkiSpace",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show($"启动管理员进程失败：{ex.Message}", "AkiSpace",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动管理员进程失败：{ex.Message}", "AkiSpace",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}