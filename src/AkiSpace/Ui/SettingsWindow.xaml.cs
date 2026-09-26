using System.Windows;
using System.Windows.Forms;
using AkiSpace.Services;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;

namespace AkiSpace.Ui;

/// <summary>
/// Settings window. Saving writes all AppSettings fields through
/// SettingsService.Update; the password field only overwrites when non-empty,
/// so saving without typing leaves the stored password untouched.
/// </summary>
public partial class SettingsWindow : ChromeWindow
{
    private readonly SettingsService _settingsService;

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = _settingsService.Current;
        RdoChild.IsChecked = s.ConnectionMode == ConnectionMode.ChildSession;
        RdoStandard.IsChecked = s.ConnectionMode != ConnectionMode.ChildSession;
        NumWidth.Value = s.DesktopWidth;
        NumHeight.Value = s.DesktopHeight;
        NumColorDepth.Value = s.ColorDepth;
        NumPort.Value = s.RdpPort;
        ChkSmartSizing.IsChecked = s.SmartSizing;
        ChkShortcuts.IsChecked = s.SendSystemShortcutsToRemote;
        ChkAudio.IsChecked = s.AudioRedirected;
        ChkAutoConnect.IsChecked = s.AutoConnect;
        ChkLogoff.IsChecked = s.LogoffOnExit;
        ChkMinimizeTray.IsChecked = s.MinimizeToTray;
        ChkPerformance.IsChecked = s.ShowPerformance;
        ChkHotkeys.IsChecked = s.EnableGlobalHotkey;
        ChkGameMouse.IsChecked = s.GameMouseModeEnabled;
        TxtUsername.Text = s.CloneUsername;
        TxtLaunchPath.Text = s.LaunchProgramPath ?? string.Empty;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Title = "选择连接后自动启动的程序",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        };
        if (ofd.ShowDialog() == WinFormsDialogResult.OK)
            TxtLaunchPath.Text = ofd.FileName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // PasswordBox never displays the stored password; empty means "keep it".
        var newPassword = PbPassword.Password;
        var path = string.IsNullOrWhiteSpace(TxtLaunchPath.Text) ? null : TxtLaunchPath.Text;
        _settingsService.Update(s =>
        {
            s.ConnectionMode = RdoChild.IsChecked == true
                ? ConnectionMode.ChildSession
                : ConnectionMode.StandardRdp;
            s.DesktopWidth = NumWidth.Value;
            s.DesktopHeight = NumHeight.Value;
            s.ColorDepth = NumColorDepth.Value;
            s.RdpPort = NumPort.Value;
            s.SmartSizing = ChkSmartSizing.IsChecked == true;
            s.SendSystemShortcutsToRemote = ChkShortcuts.IsChecked == true;
            s.AudioRedirected = ChkAudio.IsChecked == true;
            s.AutoConnect = ChkAutoConnect.IsChecked == true;
            s.LogoffOnExit = ChkLogoff.IsChecked == true;
            s.MinimizeToTray = ChkMinimizeTray.IsChecked == true;
            s.ShowPerformance = ChkPerformance.IsChecked == true;
            s.EnableGlobalHotkey = ChkHotkeys.IsChecked == true;
            s.GameMouseModeEnabled = ChkGameMouse.IsChecked == true;
            s.CloneUsername = TxtUsername.Text;
            if (!string.IsNullOrEmpty(newPassword))
                s.ClonePassword = newPassword;
            s.LaunchProgramPath = path;
        });
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
