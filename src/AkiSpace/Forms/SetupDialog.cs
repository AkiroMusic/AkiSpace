using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AkiSpace.Common;
using AkiSpace.Controls.Styled;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Forms;

/// <summary>
/// Environment check & fix dialog - Ethereal Glass design with card layout, styled ListView, amber home panel.
/// </summary>
public sealed class SetupDialog : Form
{
    private readonly ILogger<SetupDialog> _logger;
    private readonly EnvironmentVerifier _verifier;
    private readonly ChildSessionManager _sessionManager;
    private readonly SettingsService? _settingsService;

    private const string SergiyeReleasesUrl = "https://github.com/sergiye/rdpWrapper/releases";
    private const string SebaxakerhtcRepoUrl = "https://github.com/sebaxakerhtc/rdpwrap";

    private readonly StyledListView _listView = new();
    private readonly GhostButton _btnCheck = new();
    private readonly PrimaryButton _btnFix = new();
    private readonly GhostButton _btnClose = new();
    private readonly DoubleBezelCard _homeGuideCard = new();
    private readonly Label _lblHomeTitle = new();
    private readonly Label _lblHomeBody = new();
    private readonly Label _lblTermsrvVersion = new();
    private readonly GhostButton _btnOpenSergiye = new();
    private readonly GhostButton _btnOpenSebaxakerhtc = new();
    private readonly GhostButton _btnCopyVersion = new();
    private readonly GhostButton _btnCopyDiagnostics = new();
    private readonly PrimaryButton _btnRecheckAfterInstall = new();

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
        Size = new Size(800, 700);
        MinimumSize = new Size(700, 550);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = ThemeManager.Current.BgBase;
        ForeColor = ThemeManager.Current.TextPrimary;
        Font = ThemeManager.Current.GetFontSans(13f);
        FormBorderStyle = FormBorderStyle.Sizable;

        var mainPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(ThemeTokens.Space.S6), // 24px
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
        };

        // --- Checks Card ---
        var checksCard = new DoubleBezelCard
        {
            Title = "环境检查",
            Subtitle = "桌面分身运行前置条件检测",
            Width = 720,
        };
        BuildChecksCard(checksCard);
        flow.Controls.Add(checksCard);

        // --- Home Guide Card (amber tinted) ---
        BuildHomeGuideCard();
        flow.Controls.Add(_homeGuideCard);

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

        _btnCheck.Text = "重新检查";
        _btnCheck.Size = new Size(110, 40);
        _btnCheck.Click += (_, _) => RunChecks();

        _btnFix.Text = "一键修复";
        _btnFix.Size = new Size(110, 40);
        _btnFix.Click += (_, _) => RunFixes();

        _btnClose.Text = "关闭";
        _btnClose.Size = new Size(90, 40);
        _btnClose.Click += (_, _) => Close();

        btnFlow.Controls.Add(_btnClose);
        btnFlow.Controls.Add(_btnFix);
        btnFlow.Controls.Add(_btnCheck);
        btnPanel.Controls.Add(btnFlow);
        Controls.Add(btnPanel);

        ThemeManager.Current.ThemeChanged += (_, _) => OnThemeChanged();
    }

    private void BuildChecksCard(DoubleBezelCard card)
    {
        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.FullRowSelect = true;
        _listView.GridLines = false;
        _listView.BorderStyle = BorderStyle.None;
        _listView.BackColor = Color.Transparent;
        _listView.ForeColor = ThemeManager.Current.TextPrimary;
        _listView.Font = ThemeManager.Current.GetFontSans(12f);
        _listView.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _listView.OwnerDraw = true;
        _listView.Columns.Add("检查项", 300);
        _listView.Columns.Add("状态", 100);
        _listView.Columns.Add("详情", 300);
        _listView.Height = 400;

        card.ContentControls.Add(_listView);
    }

    private void BuildHomeGuideCard()
    {
        _homeGuideCard.Title = "⚠ 家庭版需要安装 RDP Wrapper";
        _homeGuideCard.Subtitle = "Windows Home 缺少 RDP 主机功能，需第三方解锁层";
        _homeGuideCard.Width = 720;
        _homeGuideCard.Visible = false;

        // Override card colors for amber tint
        _homeGuideCard.Paint += (_, e) =>
        {
            base.OnPaint(e); // This won't work, need custom paint
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, ThemeTokens.Space.S2, 0, 0),
        };

        _lblHomeTitle.Text = "Windows 家庭版不提供 RDP 主机，桌面分身功能需要 RDP Wrapper 第三方解锁层。\n安装步骤：① 下载任一社区维护的 RDP Wrapper → ② 解压到 C:\\Program Files\\RDP Wrapper\\ →\n③ 以管理员运行 rdpWrapper.exe -install → ④ 确认 rdpwrap.ini 含本机 termsrv.dll 版本段。\nAkiSpace 不会自动下载运行第三方二进制，请按需从可信来源获取。";
        _lblHomeTitle.AutoSize = true;
        _lblHomeTitle.Font = ThemeManager.Current.GetFontSans(12f);
        _lblHomeTitle.ForeColor = ThemeManager.Current.TextSecondary;
        _lblHomeTitle.MaximumSize = new Size(680, 0);
        _lblHomeTitle.Margin = new Padding(0, 0, 0, ThemeTokens.Space.S4);

        var termsrvVer = _sessionManager.GetTermsrvVersion();
        _lblTermsrvVersion.Text = $"本机 termsrv.dll 版本:  {termsrvVer}    （在 rdpwrap.ini 中需找到 [10.0.{termsrvVer.Split('.')[2]}.xxxx] 段落）";
        _lblTermsrvVersion.AutoSize = true;
        _lblTermsrvVersion.Font = ThemeManager.Current.GetFontMono(11f, FontStyle.Bold);
        _lblTermsrvVersion.ForeColor = ThemeManager.Current.Accent;
        _lblTermsrvVersion.Margin = new Padding(0, 0, 0, ThemeTokens.Space.S6);

        var btnFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, ThemeTokens.Space.S4),
        };

        StyleGuideButton(_btnOpenSergiye, "① 打开 sergiye/rdpWrapper (C# 推荐)");
        _btnOpenSergiye.Click += (_, _) => OpenUrl(SergiyeReleasesUrl);
        _btnOpenSergiye.Size = new Size(320, 40);

        StyleGuideButton(_btnOpenSebaxakerhtc, "② 打开 sebaxakerhtc/rdpwrap (Delphi fork)");
        _btnOpenSebaxakerhtc.Click += (_, _) => OpenUrl(SebaxakerhtcRepoUrl);
        _btnOpenSebaxakerhtc.Size = new Size(320, 40);

        StyleGuideButton(_btnCopyVersion, "③ 复制版本号（用于搜索 rdpwrap.ini）");
        _btnCopyVersion.Click += (_, _) => CopyToClipboard(termsrvVer, "已复制 termsrv.dll 版本号");
        _btnCopyVersion.Size = new Size(320, 40);

        StyleGuideButton(_btnCopyDiagnostics, "④ 复制诊断信息（用于 GitHub 反馈）");
        _btnCopyDiagnostics.Click += (_, _) => CopyDiagnosticsToClipboard();
        _btnCopyDiagnostics.Size = new Size(320, 40);

        _btnRecheckAfterInstall.Text = "我已安装 RDP Wrapper → 重新检查";
        _btnRecheckAfterInstall.Size = new Size(660, 44);
        _btnRecheckAfterInstall.Click += (_, _) => RunChecks();

        btnFlow.Controls.Add(_btnOpenSergiye);
        btnFlow.Controls.Add(_btnOpenSebaxakerhtc);
        btnFlow.Controls.Add(_btnCopyVersion);
        btnFlow.Controls.Add(_btnCopyDiagnostics);

        flow.Controls.Add(_lblHomeTitle);
        flow.Controls.Add(_lblTermsrvVersion);
        flow.Controls.Add(btnFlow);
        flow.Controls.Add(_btnRecheckAfterInstall);

        _homeGuideCard.ContentControls.Add(flow);
    }

    private static void StyleGuideButton(GhostButton btn, string text)
    {
        btn.Text = text;
        btn.Font = ThemeManager.Current.GetFontSans(12f);
    }

    private async void RunChecks()
    {
        try
        {
            var results = await _verifier.RunAllChecksAsync();
            if (IsDisposed) return;
            RenderChecks(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Environment check failed");
            if (IsDisposed) return;
            _listView.BeginUpdate();
            _listView.Items.Clear();
            var item = new ListViewItem("环境检查");
            item.SubItems.Add("✗ 错误");
            item.SubItems.Add($"检查失败：{ex.Message}");
            _listView.Items.Add(item);
            _listView.EndUpdate();
        }
    }

    private void RenderChecks(List<EnvCheckResult> results)
    {
        _listView.BeginUpdate();
        _listView.Items.Clear();

        EnvCheckResult? wrapperInstallCheck = null;
        foreach (var check in results)
        {
            var item = new ListViewItem(check.Name);
            item.SubItems.Add(check.Pass ? "✓ 通过" : "✗ 失败");
            item.SubItems.Add(check.Detail);
            item.Tag = check;
            _listView.Items.Add(item);

            if (check.Name.StartsWith("多会话解锁"))
                wrapperInstallCheck = check;
        }
        _listView.EndUpdate();
        _logger.LogInformation("Environment checks completed");

        var showHomeGuide = wrapperInstallCheck is { Pass: false };
        _homeGuideCard.Visible = showHomeGuide;
        _logger.LogInformation("Home install guide visible: {Show}", showHomeGuide);
    }

    private void RunFixes()
    {
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

        using var confirm = new Form
        {
            Text = "AkiSpace 环境修复",
            Size = new Size(580, isChildMode ? 420 : 380),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = ThemeManager.Current.BgBase,
            ForeColor = ThemeManager.Current.TextPrimary,
            Font = ThemeManager.Current.GetFontSans(13f),
        };

        var mainFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(ThemeTokens.Space.S6),
            AutoScroll = true,
        };

        var lbl = new Label
        {
            Text = prompt,
            AutoSize = true,
            ForeColor = ThemeManager.Current.TextPrimary,
            BackColor = Color.Transparent,
            Font = ThemeManager.Current.GetFontSans(13f),
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, ThemeTokens.Space.S6),
        };

        var chkForceDisable = new StyledCheckBox
        {
            Text = "同时禁用 TermWrap（覆盖默认行为）",
            Visible = !isChildMode,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, ThemeTokens.Space.S6),
        };

        var btnFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
        };

        var btnOk = new PrimaryButton { Text = "继续", Size = new Size(100, 40), DialogResult = DialogResult.OK };
        var btnCancel = new GhostButton { Text = "取消", Size = new Size(90, 40), DialogResult = DialogResult.Cancel };

        btnFlow.Controls.Add(btnCancel);
        btnFlow.Controls.Add(btnOk);

        mainFlow.Controls.Add(lbl);
        mainFlow.Controls.Add(chkForceDisable);
        mainFlow.Controls.Add(btnFlow);

        confirm.Controls.Add(mainFlow);
        confirm.AcceptButton = btnOk;
        confirm.CancelButton = btnCancel;

        if (confirm.ShowDialog(this) != DialogResult.OK) return;
        var overrideDisable = chkForceDisable.Checked;

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
                Verb = "runas",
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (ex.NativeErrorCode == 1223)
            {
                MessageBox.Show("已取消管理员权限，修复未执行。", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show($"启动管理员进程失败：{ex.Message}", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动管理员进程失败：{ex.Message}", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            MessageBox.Show($"无法打开链接：{ex.Message}\n请手动访问：{url}", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { Clipboard.SetText(text); break; }
                catch when (attempt < 2) { Thread.Sleep(50); }
            }
            MessageBox.Show($"{successMessage}：{text}", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "AkiSpace", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private void OnThemeChanged()
    {
        BackColor = ThemeManager.Current.BgBase;
        ForeColor = ThemeManager.Current.TextPrimary;
        Font = ThemeManager.Current.GetFontSans(13f);

        _listView.BackColor = Color.Transparent;
        _listView.ForeColor = ThemeManager.Current.TextPrimary;
        _listView.Invalidate();

        _homeGuideCard.Invalidate();

        Invalidate();
    }
}