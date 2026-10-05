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
using SystemColors = System.Windows.SystemColors;
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
    private readonly RdpWrapperInstaller _wrapperInstaller;

    public sealed record CheckRow(string Name, string Status, string Detail, Brush StatusBrush);

    public SetupWindow(
        ILogger<SetupWindow> logger,
        EnvironmentVerifier verifier,
        ChildSessionManager sessionManager,
        SettingsService settingsService,
        RdpWrapperInstaller? wrapperInstaller = null)
    {
        InitializeComponent();
        _logger = logger;
        _verifier = verifier;
        _sessionManager = sessionManager;
        _settingsService = settingsService;
        _wrapperInstaller = wrapperInstaller ?? AppShellServices.WrapperInstaller;

        var termsrvVer = _sessionManager.GetTermsrvVersion();
        var verParts = termsrvVer.Split('.');
        LblTermsrvVersion.Text = Loc.F("Env_Termsrv", termsrvVer, verParts.Length > 2 ? verParts[2] : "?");

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
            new(Loc.T("Env_Placeholder"), "…", Loc.T("Env_PlaceholderDetail"), ThemeBrush(WpfThemeHost.TextTertiary)),
        };
    }

    private void RenderError(Exception ex)
    {
        ChecksList.ItemsSource = new List<CheckRow>
        {
            new(Loc.T("Env_CheckCard"), Loc.T("Env_Error"), Loc.F("Env_CheckFailed", ex.Message), ThemeBrush(WpfThemeHost.Error)),
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
                check.Pass ? Loc.T("Env_Pass") : Loc.T("Env_Fail"),
                check.Detail,
                check.Pass ? PassBrush() : FailBrush()));
            if (check.Id == "WrapperUnlock" && !check.Pass)
                wrapperInstallFailed = true;
        }
        ChecksList.ItemsSource = rows;
        HomeGuideCard.Visibility = wrapperInstallFailed ? Visibility.Visible : Visibility.Collapsed;
        UpdateVerdict(results);
        _logger.LogInformation("Environment checks completed; home guide visible: {Show}", wrapperInstallFailed);
    }

    // ---------------------------------------------------------------- verdict

    /// <summary>
    /// Translates the raw checks into what the user cares about: which clone modes
    /// actually work on this machine. Standard RDP needs the listener AND (on Home)
    /// the unlock layer; child sessions need the broker switch. On a Home machine
    /// without the layer this reads as "只能用子会话" and offers the one-click install.
    /// </summary>
    private void UpdateVerdict(List<EnvCheckResult> results)
    {
        var child = results.FirstOrDefault(c => c.Id == "ChildSessions");
        var wrapper = results.FirstOrDefault(c => c.Id == "WrapperUnlock");
        var listener = results.FirstOrDefault(c => c.Id == "Listener");

        if (child is null || wrapper is null || listener is null)
        {
            TxtChildVerdict.Text = Loc.T("Env_ChildUnknown");
            DotChildVerdict.Fill = ThemeBrush(WpfThemeHost.TextTertiary);
            TxtStandardVerdict.Text = Loc.T("Env_ChildUnknown");
            DotStandardVerdict.Fill = ThemeBrush(WpfThemeHost.TextTertiary);
            ShowInstallUi(showButton: false, showNote: false, showManual: false, status: null);
            return;
        }

        TxtChildVerdict.Text = child.Pass ? Loc.T("Env_ChildOk") : Loc.T("Env_ChildNeedsFix");
        DotChildVerdict.Fill = child.Pass ? ThemeBrush(WpfThemeHost.Success) : ThemeBrush(WpfThemeHost.Warning);

        var standardOk = wrapper.Pass && listener.Pass;
        TxtStandardVerdict.Text = standardOk ? Loc.T("Env_StandardOk") : Loc.T("Env_StandardUnavailable");
        DotStandardVerdict.Fill = standardOk ? ThemeBrush(WpfThemeHost.Success) : ThemeBrush(WpfThemeHost.Error);

        var homeWithoutLayer = !wrapper.Pass;
        if (standardOk)
        {
            // Wrapper present → child sessions are refused while the hook is active.
            TxtVerdictReason.Text = Loc.T("Env_WrapperInstalledNote");
            TxtVerdictReason.Visibility = RdpWrapperInstaller.IsWrapperHookInstalled()
                ? Visibility.Visible : Visibility.Collapsed;
            ShowInstallUi(showButton: false, showNote: false, showManual: false, status: null);
        }
        else
        {
            TxtVerdictReason.Text = Loc.T("Env_StandardReason");
            TxtVerdictReason.Visibility = Visibility.Visible;
            ShowInstallUi(
                showButton: homeWithoutLayer,
                showNote: homeWithoutLayer,
                showManual: homeWithoutLayer,
                status: null);
        }
    }

    private void ShowInstallUi(bool showButton, bool showNote, bool showManual, string? status)
    {
        BtnInstallWrapper.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
        TxtInstallNote.Visibility = showNote ? Visibility.Visible : Visibility.Collapsed;
        BtnManualGuide.Visibility = showManual ? Visibility.Visible : Visibility.Collapsed;
        TxtInstallStatus.Text = status ?? string.Empty;
        TxtInstallStatus.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnShowManualGuide(object sender, RoutedEventArgs e)
    {
        HomeGuideCard.Visibility = Visibility.Visible;
        HomeGuideCard.BringIntoView();
    }

    // ---------------------------------------------------------------- one-click unlock layer install

    /// <summary>
    /// One-click RDP unlock layer install for Home editions: download the pinned
    /// official build → SHA-256 verify → run elevated (-install -offline; the upstream
    /// installer restarts TermService itself) → re-check so the verdict refreshes.
    /// </summary>
    private async void OnInstallWrapper(object sender, RoutedEventArgs e)
    {
        BtnInstallWrapper.IsEnabled = false;
        try
        {
            ShowInstallUi(showButton: true, showNote: true, showManual: true, status: Loc.F("Env_InstallDownloading", RdpWrapperInstaller.PinnedVersion));
            var exePath = await _wrapperInstaller.DownloadVerifiedAsync();
            ShowInstallUi(showButton: true, showNote: true, showManual: true, status: Loc.T("Env_InstallUac"));

            var process = Process.Start(RdpWrapperInstaller.BuildInstallStartInfo(exePath));
            if (process is null)
                throw new InvalidOperationException("installer process could not be started");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                ShowInstallUi(showButton: true, showNote: true, showManual: true,
                    status: Loc.F("Env_InstallFailed", $"rdpWrapper exit code {process.ExitCode}"));
                return;
            }

            ShowInstallUi(showButton: false, showNote: true, showManual: false, status: Loc.T("Env_InstallDone"));
            await RunChecksAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowInstallUi(showButton: true, showNote: true, showManual: true, status: Loc.T("Env_InstallUacCancelled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "One-click wrapper install failed");
            ShowInstallUi(showButton: true, showNote: true, showManual: true, status: Loc.F("Env_InstallFailed", ex.Message));
        }
        finally
        {
            BtnInstallWrapper.IsEnabled = true;
        }
    }

    private static Brush PassBrush() => ThemeBrush(WpfThemeHost.Success);
    private static Brush FailBrush() => ThemeBrush(WpfThemeHost.Error);

    private static Brush ThemeBrush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;

    private async void OnRecheck(object sender, RoutedEventArgs e) => await RunChecksAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- 一键修复

    private void OnFix(object sender, RoutedEventArgs e)
    {
        var isChildMode = _settingsService.Current.ConnectionMode == ConnectionMode.ChildSession;
        var prompt = Loc.T(isChildMode ? "Env_FixPromptChild" : "Env_FixPromptStandard");

        var confirm = new FixConfirmWindow(prompt, showOverrideOption: !isChildMode) { Owner = this };
        if (confirm.ShowDialog() != true) return;
        var overrideDisable = confirm.OverrideDisable;

        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null)
            {
                MessageBox.Show(Loc.T("Env_NoExePath"), "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(Loc.T("Env_UacCancelled"), "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(Loc.F("Env_LaunchFail", ex.Message), "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Env_LaunchFail", ex.Message), "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(Loc.F("Env_OpenUrlFail", ex.Message, url), "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCopyVersion(object sender, RoutedEventArgs e) =>
        CopyToClipboard(_sessionManager.GetTermsrvVersion(), Loc.T("Env_CopiedVersion"));

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e) => _ = CopyDiagnosticsAsync();

    /// <summary>Async: the check batch includes the multi-second listener probe and
    /// must never run on the UI thread (the sync batch would freeze this window).</summary>
    private async System.Threading.Tasks.Task CopyDiagnosticsAsync()
    {
        try
        {
            var results = await _verifier.RunAllChecksAsync();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Loc.T("Diag_Header"));
            sb.AppendLine();
            sb.AppendLine(Loc.F("Diag_Time", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
            sb.AppendLine(Loc.F("Diag_Os", Environment.OSVersion.ToString()));
            sb.AppendLine(Loc.F("Diag_Proc", Environment.Is64BitProcess ? "x64" : "x86",
                Environment.Is64BitOperatingSystem ? "64-bit OS" : "32-bit OS"));
            sb.AppendLine();
            sb.AppendLine(Loc.T("Diag_Section"));
            foreach (var c in results)
            {
                var icon = c.Pass ? "✓" : "✗";
                sb.AppendLine($"- {icon} **{c.Name}** — {c.Detail}");
            }
            CopyToClipboard(sb.ToString(), Loc.T("Env_CopiedDiag"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy diagnostics failed");
            CopyToClipboard(Loc.F("Env_CheckFailed", ex.Message), Loc.T("Env_CopiedDiag"));
        }
    }

    private static void CopyToClipboard(string text, string successMessage)
    {
        try
        {
            if (string.IsNullOrEmpty(text))
            {
                MessageBox.Show(Loc.T("Env_NothingToCopy"), "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(Loc.F("Env_CopyFail", ex.Message), "AkiSpace", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary>Elevated-fix confirmation dialog (mode-dependent prompt + TermWrap override).</summary>
public class FixConfirmWindow : ChromeWindow
{
    public bool OverrideDisable { get; private set; }

    public FixConfirmWindow(string prompt, bool showOverrideOption)
    {
        Title = Loc.T("Env_FixTitle");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource(WpfThemeHost.BgBase) as Brush
                     ?? SystemColors.ControlBrush;

        var text = new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.TryFindResource("Text.Base")!,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var chk = new CheckBox
        {
            Content = Loc.T("Env_FixOverride"),
            Style = (Style)Application.Current.TryFindResource("Input.CheckBox")!,
            Visibility = showOverrideOption ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var btnOk = new Button { Content = Loc.T("Env_Continue"), Style = (Style)Application.Current.TryFindResource("Btn.Primary")!, MinWidth = 100 };
        var btnCancel = new Button { Content = Loc.T("Set_Cancel"), Style = (Style)Application.Current.TryFindResource("Btn.Ghost")!, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
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
