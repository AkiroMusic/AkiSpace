using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using AkiSpace.Common;
using AkiSpace.Services;
using AkiSpace.Ui.Theme;
using Microsoft.Extensions.Logging;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Thickness = System.Windows.Thickness;

namespace AkiSpace.Ui;

/// <summary>
/// Environment check &amp; fix window: async environment checks, a conditional
/// home-edition installation guide and the elevated one-click fix re-launch
/// (--fix-env).
/// </summary>
public partial class SetupWindow : ChromeWindow
{
    private const string SergiyeReleasesUrl = "https://github.com/sergiye/rdpWrapper/releases";
    private const string SebaxakerhtcRepoUrl = "https://github.com/sebaxakerhtc/rdpwrap";

    private readonly ILogger<SetupWindow> _logger;
    private readonly EnvironmentVerifier _verifier;
    private readonly ChildSessionManager _sessionManager;
    private readonly SettingsService _settingsService;

    public sealed record CheckRow(string Name, string Status, string Detail, Brush StatusBrush);

    public SetupWindow(
        ILogger<SetupWindow> logger,
        EnvironmentVerifier verifier,
        ChildSessionManager sessionManager,
        SettingsService settingsService)
    {
        InitializeComponent();
        _logger = logger;
        _verifier = verifier;
        _sessionManager = sessionManager;
        _settingsService = settingsService;

        var termsrvVer = _sessionManager.GetTermsrvVersion();
        LblTermsrvVersion.Text =
            $"本机 termsrv.dll 版本:  {termsrvVer}    （在 rdpwrap.ini 中需找到 [10.0.{termsrvVer.Split('.')[2]}.xxxx] 段落）";

        _ = RunChecksAsync();
    }

    private async System.Threading.Tasks.Task RunChecksAsync()
    {
        try
        {
            RenderPlaceholder();
            var results = await _verifier.RunAllChecksAsync();
            RenderChecks(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Environment check failed");
            RenderError(ex);
        }
    }

    private void RenderPlaceholder()
    {
        ChecksList.ItemsSource = new List<CheckRow>
        {
            new("正在检查环境…", "…", "RDP 监听探测可能需要数秒，请稍候", Brushes.Gray),
        };
    }

    private void RenderError(Exception ex)
    {
        ChecksList.ItemsSource = new List<CheckRow>
        {
            new("环境检查", "✗ 错误", $"检查失败：{ex.Message}", FailBrush()),
        };
    }

    private void RenderChecks(List<EnvCheckResult> results)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => RenderChecks(results));
            return;
        }
        var rows = new List<CheckRow>(results.Count);
        var wrapperInstallFailed = false;
        foreach (var check in results)
        {
            rows.Add(new CheckRow(
                check.Name,
                check.Pass ? "✓ 通过" : "✗ 失败",
                check.Detail,
                check.Pass ? PassBrush() : FailBrush()));
            if (check.Name.StartsWith("多会话解锁") && !check.Pass)
                wrapperInstallFailed = true;
        }
        ChecksList.ItemsSource = rows;
        HomeGuideCard.Visibility = wrapperInstallFailed ? Visibility.Visible : Visibility.Collapsed;
        _logger.LogInformation("Environment checks completed; home guide visible: {Show}", wrapperInstallFailed);
    }

    private static Brush PassBrush() => ThemeBrush(WpfThemeHost.Success);
    private static Brush FailBrush() => ThemeBrush(WpfThemeHost.Error);

    private static Brush ThemeBrush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    private async void OnRecheck(object sender, RoutedEventArgs e) => await RunChecksAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- 一键修复

    private void OnFix(object sender, RoutedEventArgs e)
    {
        var isChildMode = _settingsService.Current.ConnectionMode == ConnectionMode.ChildSession;

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

        var confirm = new FixConfirmWindow(prompt, showOverrideOption: !isChildMode) { Owner = this };
        if (confirm.ShowDialog() != true) return;
        var overrideDisable = confirm.OverrideDisable;

        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null)
            {
                MessageBox.Show("无法获取程序路径。", "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show("已取消管理员权限，修复未执行。", "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"启动管理员进程失败：{ex.Message}", "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动管理员进程失败：{ex.Message}", "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- guide helpers

    private void OnOpenSergiye(object sender, RoutedEventArgs e) => OpenUrl(SergiyeReleasesUrl);
    private void OnOpenSebaxakerhtc(object sender, RoutedEventArgs e) => OpenUrl(SebaxakerhtcRepoUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开链接：{ex.Message}\n请手动访问：{url}", "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCopyVersion(object sender, RoutedEventArgs e) =>
        CopyToClipboard(_sessionManager.GetTermsrvVersion(), "已复制 termsrv.dll 版本号");

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
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

    private static void CopyToClipboard(string text, string successMessage)
    {
        try
        {
            if (string.IsNullOrEmpty(text))
            {
                MessageBox.Show("无内容可复制。", "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { Clipboard.SetText(text); break; }
                catch when (attempt < 2) { System.Threading.Thread.Sleep(50); }
            }
            MessageBox.Show($"{successMessage}：{text}", "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary>Elevated-fix confirmation dialog (mode-dependent prompt + TermWrap override).</summary>
public class FixConfirmWindow : ChromeWindow
{
    public bool OverrideDisable { get; private set; }

    public FixConfirmWindow(string prompt, bool showOverrideOption)
    {
        Title = "AkiSpace 环境修复";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource(WpfThemeHost.BgBase) as Brush
                     ?? System.Windows.Media.Brushes.Black;

        var text = new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.TryFindResource("Text.Base")!,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var chk = new CheckBox
        {
            Content = "同时禁用 TermWrap（覆盖默认行为）",
            Style = (Style)Application.Current.TryFindResource("Input.CheckBox")!,
            Visibility = showOverrideOption ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var btnOk = new Button { Content = "继续", Style = (Style)Application.Current.TryFindResource("Btn.Primary")!, MinWidth = 100 };
        var btnCancel = new Button { Content = "取消", Style = (Style)Application.Current.TryFindResource("Btn.Ghost")!, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        btnOk.IsDefault = true;
        btnCancel.IsCancel = true;

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnPanel.Children.Add(btnCancel);
        btnPanel.Children.Add(btnOk);

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(text);
        root.Children.Add(chk);
        root.Children.Add(btnPanel);
        Content = root;

        btnOk.Click += (_, _) =>
        {
            OverrideDisable = chk.IsChecked == true;
            DialogResult = true;
            Close();
        };
    }
}
