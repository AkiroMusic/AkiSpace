using System.Diagnostics;
using AkiSpace.Common;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Forms;

/// <summary>
/// One-click environment check &amp; fix dialog for the desktop-clone prerequisites:
/// RDP enabled, multi-session, RDP Wrapper (Home), StartRCM, TermService,
/// firewall loopback rule, child sessions, listener, termsrv version.
///
/// Home edition users get an additional guided install panel for RDP Wrapper
/// (sergiye/rdpWrapper or sebaxakerhtc/rdpwrap), which must be installed manually
/// because it patches / hooks TermService's termsrv.dll.
public sealed class SetupDialog : Form
{
    private readonly ILogger<SetupDialog> _logger;
    private readonly EnvironmentVerifier _verifier;
    private readonly ChildSessionManager _sessionManager;
    private readonly SettingsService? _settingsService;

    // Known RDP Wrapper source repos
    private const string SergiyeReleasesUrl = "https://github.com/sergiye/rdpWrapper/releases";
    private const string SebaxakerhtcRepoUrl = "https://github.com/sebaxakerhtc/rdpwrap";

    private readonly ListView _listView = new();
    private readonly Button _btnCheck = new();
    private readonly Button _btnFix = new();
    private readonly Button _btnClose = new();
    private readonly Label _lblHint = new();

    // ---- Home RDP Wrapper install guide panel ----
    private readonly Panel _panelHomeGuide = new();
    private readonly Label _lblHomeTitle = new();
    private readonly Label _lblHomeBody = new();
    private readonly Label _lblTermsrvVersion = new();
    private readonly Button _btnOpenSergiye = new();
    private readonly Button _btnOpenSebaxakerhtc = new();
    private readonly Button _btnCopyVersion = new();
    private readonly Button _btnCopyDiagnostics = new();
    private readonly Button _btnRecheckAfterInstall = new();

    public SetupDialog(
        ILogger<SetupDialog> logger,
        EnvironmentVerifier verifier,
        ChildSessionManager sessionManager,
        SettingsService? settingsService = null)
    {
        _logger = logger;
        _verifier = verifier;
        _sessionManager = sessionManager;
        _settingsService = settingsService;

        BuildUi();
        RunChecks();
    }

    private void BuildUi()
    {
        Text = "环境检查 / 修复 — 桌面分身前置条件";
        Size = new Size(760, 640);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);

        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.FullRowSelect = true;
        _listView.GridLines = true;
        _listView.BackColor = Theme.Control;
        _listView.ForeColor = Theme.Text;
        _listView.BorderStyle = BorderStyle.None;
        _listView.Columns.Add("检查项", 260);
        _listView.Columns.Add("状态", 90);
        _listView.Columns.Add("详情", 360);

        // ---- Home RDP Wrapper guide panel (auto-shows on failure) ----
        BuildHomeGuidePanel();

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 100,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Theme.Surface
        };
        _lblHint.Dock = DockStyle.Top;
        _lblHint.Height = 40;
        _lblHint.Text = "提示：家庭版 Windows 需要 RDP Wrapper 解锁多会话。\n" +
                        "「一键修复」会修改注册表并重启 TermService，需要管理员权限。";
        _lblHint.ForeColor = Theme.TextDim;
        _lblHint.BackColor = Theme.Surface;

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface
        };
        _btnCheck.Text = "重新检查";
        _btnCheck.Size = new Size(110, 36);
        _btnCheck.FlatStyle = FlatStyle.Flat;
        _btnCheck.BackColor = Theme.Control;
        _btnCheck.ForeColor = Theme.Text;
        _btnCheck.FlatAppearance.BorderColor = Theme.Border;
        _btnCheck.Click += (_, _) => RunChecks();
        _btnFix.Text = "一键修复";
        _btnFix.Size = new Size(110, 36);
        _btnFix.FlatStyle = FlatStyle.Flat;
        _btnFix.BackColor = Theme.AccentBlue;
        _btnFix.ForeColor = Color.White;
        _btnFix.FlatAppearance.BorderSize = 0;
        _btnFix.Click += (_, _) => RunFixes();
        _btnClose.Text = "关闭";
        _btnClose.Size = new Size(90, 36);
        _btnClose.FlatStyle = FlatStyle.Flat;
        _btnClose.BackColor = Theme.Control;
        _btnClose.ForeColor = Theme.Text;
        _btnClose.FlatAppearance.BorderColor = Theme.Border;
        _btnClose.Click += (_, _) => Close();
        btnRow.Controls.Add(_btnClose);
        btnRow.Controls.Add(_btnFix);
        btnRow.Controls.Add(_btnCheck);

        bottom.Controls.Add(_lblHint);
        bottom.Controls.Add(btnRow);

        // Dock order matters: last-added docks fill remaining space.
        // We want: top = home guide (when shown), fill = list, bottom = bottom.
        // Using Top dock for the guide means we need to add it BEFORE the list (Fill).
        Controls.Add(_panelHomeGuide);   // DockStyle.Top, added second-to-last
        Controls.Add(_listView);          // DockStyle.Fill, fills remaining
        Controls.Add(bottom);             // DockStyle.Bottom
    }

    private void BuildHomeGuidePanel()
    {
        _panelHomeGuide.Dock = DockStyle.Top;
        _panelHomeGuide.Visible = false;
        _panelHomeGuide.BackColor = Color.FromArgb(45, 35, 20);  // amber-tinted dark
        _panelHomeGuide.BorderStyle = BorderStyle.FixedSingle;
        _panelHomeGuide.Padding = new Padding(14, 12, 14, 12);
        _panelHomeGuide.AutoSize = true;
        _panelHomeGuide.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        // Title row
        _lblHomeTitle.Text = "⚠ 家庭版需要安装 RDP Wrapper";
        _lblHomeTitle.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        _lblHomeTitle.ForeColor = Theme.AccentAmber;
        _lblHomeTitle.AutoSize = true;
        _lblHomeTitle.Location = new Point(0, 0);

        // Body text with the actual version number
        _lblHomeBody.Text =
            "Windows 家庭版不提供 RDP 主机，桌面分身功能需要 RDP Wrapper 第三方解锁层。\n" +
            "安装步骤：① 下载任一社区维护的 RDP Wrapper → ② 解压到 C:\\Program Files\\RDP Wrapper\\ →\n" +
            "③ 以管理员运行 rdpWrapper.exe -install → ④ 确认 rdpwrap.ini 含本机 termsrv.dll 版本段。\n" +
            "AkiSpace 不会自动下载运行第三方二进制，请按需从可信来源获取。";
        _lblHomeBody.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        _lblHomeBody.ForeColor = Theme.Text;
        _lblHomeBody.Location = new Point(0, 28);
        _lblHomeBody.AutoSize = true;
        _lblHomeBody.MaximumSize = new Size(720, 0);

        // Version row (highlighted)
        var termsrvVer = _sessionManager.GetTermsrvVersion();
        _lblTermsrvVersion.Text = $"本机 termsrv.dll 版本:  {termsrvVer}    （在 rdpwrap.ini 中需找到 [10.0.{termsrvVer.Split('.')[2]}.xxxx] 段落）";
        _lblTermsrvVersion.Font = new Font("Consolas", 9.5f, FontStyle.Bold);
        _lblTermsrvVersion.ForeColor = Theme.AccentBlue;
        _lblTermsrvVersion.Location = new Point(0, 100);
        _lblTermsrvVersion.AutoSize = true;

        // Button row
        int y = 130;
        StyleGuideButton(_btnOpenSergiye, "① 打开 sergiye/rdpWrapper (C# 推荐)", 290, 32);
        _btnOpenSergiye.Location = new Point(0, y);
        _btnOpenSergiye.Click += (_, _) => OpenUrl(SergiyeReleasesUrl);

        StyleGuideButton(_btnOpenSebaxakerhtc, "② 打开 sebaxakerhtc/rdpwrap (Delphi fork)", 290, 32);
        _btnOpenSebaxakerhtc.Location = new Point(300, y);
        _btnOpenSebaxakerhtc.Click += (_, _) => OpenUrl(SebaxakerhtcRepoUrl);

        y += 40;
        StyleGuideButton(_btnCopyVersion, "③ 复制版本号（用于搜索 rdpwrap.ini）", 290, 32);
        _btnCopyVersion.Location = new Point(0, y);
        _btnCopyVersion.Click += (_, _) => CopyToClipboard(termsrvVer, "已复制 termsrv.dll 版本号");

        StyleGuideButton(_btnCopyDiagnostics, "④ 复制诊断信息（用于 GitHub 反馈）", 290, 32);
        _btnCopyDiagnostics.Location = new Point(300, y);
        _btnCopyDiagnostics.Click += (_, _) => CopyDiagnosticsToClipboard();

        y += 40;
        StyleGuideButton(_btnRecheckAfterInstall, "我已安装 RDP Wrapper → 重新检查", 290, 32);
        _btnRecheckAfterInstall.Location = new Point(0, y);
        _btnRecheckAfterInstall.BackColor = Theme.AccentGreen;
        _btnRecheckAfterInstall.ForeColor = Color.White;
        _btnRecheckAfterInstall.FlatAppearance.BorderSize = 0;
        _btnRecheckAfterInstall.Click += (_, _) => RunChecks();

        // Resize panel to fit content
        _panelHomeGuide.Controls.Add(_lblHomeTitle);
        _panelHomeGuide.Controls.Add(_lblHomeBody);
        _panelHomeGuide.Controls.Add(_lblTermsrvVersion);
        _panelHomeGuide.Controls.Add(_btnOpenSergiye);
        _panelHomeGuide.Controls.Add(_btnOpenSebaxakerhtc);
        _panelHomeGuide.Controls.Add(_btnCopyVersion);
        _panelHomeGuide.Controls.Add(_btnCopyDiagnostics);
        _panelHomeGuide.Controls.Add(_btnRecheckAfterInstall);
    }

    private static void StyleGuideButton(Button btn, string text, int width, int height)
    {
        btn.Text = text;
        btn.Size = new Size(width, height);
        btn.FlatStyle = FlatStyle.Flat;
        btn.BackColor = Color.FromArgb(60, 60, 60);
        btn.ForeColor = Color.FromArgb(220, 220, 220);
        btn.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        btn.Cursor = Cursors.Hand;
    }

    private async void RunChecks()
    {
        // Run the fast checks synchronously so the list paints immediately.
        // The slow listener probe runs in the background and the list is
        // refreshed when it completes — this avoids a multi-second UI freeze
        // when the listener is cold.
        var results = await _verifier.RunAllChecksAsync();
        if (IsDisposed) return;
        RenderChecks(results);
    }

    private void RenderChecks(List<EnvCheckResult> results)
    {
        _listView.BeginUpdate();
        _listView.Items.Clear();
        // Track the *install-detection* check (CheckRdpWrapper, named "多会话解锁 ..."),
        // NOT the CheckRdpWrapperHook check (named "RDP Wrapper (TermWrap.dll) hook")
        // which has a different meaning (it checks whether the hook is currently
        // ACTIVE in the running TermService, not whether the wrapper is installed).
        EnvCheckResult? wrapperInstallCheck = null;
        foreach (var check in results)
        {
            var item = new ListViewItem(check.Name);
            item.SubItems.Add(check.Pass ? "✓ 通过" : "✗ 失败");
            item.SubItems.Add(check.Detail);
            item.ForeColor = check.Pass ? Theme.AccentGreen : Theme.AccentRed;
            _listView.Items.Add(item);
            if (check.Name.StartsWith("多会话解锁"))
                wrapperInstallCheck = check;
        }
        _listView.EndUpdate();
        _logger.LogInformation("Environment checks completed");

        // Show the Home install guide only when the wrapper install check fails
        // (i.e. no TermWrap.dll/rdpwrap.dll hook present in TermService\Parameters).
        // Pro/Enterprise users have native RDP and won't trigger this.
        var showHomeGuide = wrapperInstallCheck is { Pass: false };
        _panelHomeGuide.Visible = showHomeGuide;
        _logger.LogInformation("Home install guide visible: {Show}", showHomeGuide);
    }

    private void RunFixes()
    {
        // Ask the user up front whether to disable the TermWrap hook — this is
        // mutually exclusive with the standard-RDP Home setup, so the default
        // must reflect the user's actual intent in Settings.
        var settings = _settingsService?.Current;
        var isChildMode = settings?.ConnectionMode == ConnectionMode.ChildSession;
        var prompt = isChildMode
            ? "将执行以下操作（需要管理员权限，会弹出 UAC 提示）：\n\n" +
              "  ✓ 禁用 RDP Wrapper (TermWrap.dll)\n" +
              "  ✓ 启用 RDP（fDenyTSConnections=0）\n" +
              "  ✓ 允许多会话（fSingleSessionPerUser=0）\n" +
              "  ✓ 设置 StartRCM=1（家庭版修复）\n" +
              "  ✓ 安全加固（TLS + 高加密 + NLA）\n" +
              "  ✓ 添加防火墙回环规则（RDP 仅允许 127.0.0.1）\n" +
              "  ✓ 防火墙阻断 3389 公网入站（删除默认 RDP 公开 allow）\n" +
              "  ✓ 重启 TermService 服务（会踢掉现有 RDP 会话）\n" +
              "  ✓ 启用子会话\n\n" +
              "RDP Wrapper 与子会话模式互斥（BetterGI 官方文档已说明），\n" +
              "本工具将禁用 TermWrap 并恢复原生 termsrv.dll 以启用子会话。\n\n" +
              "注意：RDP Wrapper 本身（rdpwrap.dll）需按本对话框顶部的指引手动安装。\n\n" +
              "是否继续？"
            : "将执行以下操作（需要管理员权限，会弹出 UAC 提示）：\n\n" +
              "  ✗ 保留 RDP Wrapper hook（标准 RDP 模式必需）\n" +
              "  ✓ 启用 RDP（fDenyTSConnections=0）\n" +
              "  ✓ 允许多会话（fSingleSessionPerUser=0）\n" +
              "  ✓ 设置 StartRCM=1（家庭版修复）\n" +
              "  ✓ 安全加固（TLS + 高加密 + NLA）\n" +
              "  ✓ 添加防火墙回环规则（RDP 仅允许 127.0.0.1）\n" +
              "  ✓ 防火墙阻断 3389 公网入站（删除默认 RDP 公开 allow）\n" +
              "  ✓ 重启 TermService 服务（会踢掉现有 RDP 会话）\n\n" +
              "注意：RDP Wrapper 本身（rdpwrap.dll）需按本对话框顶部的指引手动安装。\n" +
              "如果你想在标准 RDP 模式下也禁用 TermWrap，请先在「设置」中切换到「子会话」模式，\n" +
              "或勾选下方的「同时禁用 TermWrap」选项。\n\n" +
              "是否继续？";

        // Build a confirmation dialog with an optional override checkbox.
        // We use a small inline Form rather than a YesNo MessageBox so we can
        // capture the override state.
        using var confirm = new Form
        {
            Text = "AkiSpace 环境修复",
            Size = new Size(560, 320),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        var lbl = new Label
        {
            Text = prompt,
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 4),
            ForeColor = Theme.Text,
            BackColor = Theme.Bg,
        };
        var chkForceDisable = new CheckBox
        {
            Text = "同时禁用 TermWrap（覆盖默认行为）",
            Visible = !isChildMode,  // hide the checkbox in child-session mode (already disabled by default)
            AutoSize = true,
            Dock = DockStyle.Bottom,
            Padding = new Padding(16, 0, 16, 4),
            ForeColor = Theme.TextDim,
            BackColor = Theme.Bg,
        };
        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Surface,
            Padding = new Padding(8),
        };
        var btnOk = new Button
        {
            Text = "继续",
            Size = new Size(100, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.AccentBlue,
            ForeColor = Color.White,
            FlatAppearance = { BorderSize = 0 },
            DialogResult = DialogResult.OK,
        };
        var btnCancel = new Button
        {
            Text = "取消",
            Size = new Size(90, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Control,
            ForeColor = Theme.Text,
            FlatAppearance = { BorderColor = Theme.Border },
            DialogResult = DialogResult.Cancel,
        };
        btnRow.Controls.Add(btnCancel);
        btnRow.Controls.Add(btnOk);
        confirm.Controls.Add(lbl);
        confirm.Controls.Add(chkForceDisable);
        confirm.Controls.Add(btnRow);
        confirm.AcceptButton = btnOk;
        confirm.CancelButton = btnCancel;

        if (confirm.ShowDialog(this) != DialogResult.OK) return;
        var overrideDisable = chkForceDisable.Checked;

        // Re-launch the app elevated with --fix-env flag.
        // The elevated process shows a console with fix results, then exits.
        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null)
            {
                MessageBox.Show("无法获取程序路径。", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var args = overrideDisable ? "--fix-env --disable-wrapper" : "--fix-env";
            var psi = new ProcessStartInfo(exePath, args)
            {
                Verb = "runas",          // UAC elevation
                UseShellExecute = true,  // Required for Verb
            };
            Process.Start(psi);
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

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开链接：{ex.Message}\n请手动访问：{url}", "AkiSpace",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void CopyToClipboard(string text, string successMessage)
    {
        try
        {
            if (string.IsNullOrEmpty(text))
            {
                MessageBox.Show("无内容可复制。", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // Clipboard.SetText may fail transiently if another app holds the clipboard
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { Clipboard.SetText(text); break; }
                catch when (attempt < 2) { System.Threading.Thread.Sleep(50); }
            }
            MessageBox.Show($"{successMessage}：{text}", "AkiSpace",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "AkiSpace",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopyDiagnosticsToClipboard()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## AkiSpace 诊断信息");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 系统：{Environment.OSVersion}");
        sb.AppendLine($"- 进程：{(Environment.Is64BitProcess ? "x64" : "x86")} {(Environment.Is64BitOperatingSystem ? "64-bit OS" : "32-bit OS")}");
        sb.AppendLine();
        sb.AppendLine("### 环境检查结果");
        foreach (var c in _verifier.RunAllChecks())
        {
            var icon = c.Pass ? "✓" : "✗";
            sb.AppendLine($"- {icon} **{c.Name}** — {c.Detail}");
        }
        CopyToClipboard(sb.ToString(), "诊断信息已复制（可粘贴到 GitHub issue）");
    }
}
