using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using AkiSpace.Common;
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

    /// <summary>Live preview: applies the picked color pack immediately without persisting it.</summary>
    private void OnThemePreview(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string theme })
        {
            ThemeManager.Current.SetTheme(theme, persist: false);
        }
    }

    /// <summary>Live preview: switches the whole interface immediately without persisting.</summary>
    private void OnLanguagePreview(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string language })
        {
            Loc.SetLanguage(language, persist: false);
        }
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
        _originalTheme = s.Theme;
        SelectThemeRadio(s.Theme);
        _originalLanguage = s.Language;
        (s.Language == Loc.Chinese ? RdoLangZh : RdoLangEn).IsChecked = true;
    }

    private string _originalTheme = "dark";
    private string _originalLanguage = Loc.English;

    private void SelectThemeRadio(string theme)
    {
        var radio = theme switch
        {
            "amber" => RdoThemeAmber,
            "mint" => RdoThemeMint,
            "pearl" => RdoThemePearl,
            _ => RdoThemeDark,
        };
        radio.IsChecked = true;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Title = Loc.T("Set_DlgTitle"),
            Filter = Loc.T("Set_DlgFilter"),
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
        var theme = RdoThemeAmber.IsChecked == true ? "amber"
            : RdoThemeMint.IsChecked == true ? "mint"
            : RdoThemePearl.IsChecked == true ? "pearl" : "dark";
        ThemeManager.Current.SetTheme(theme, persist: true);
        var language = RdoLangZh.IsChecked == true ? Loc.Chinese : Loc.English;
        Loc.SetLanguage(language, persist: true);
        _saved = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        ThemeManager.Current.SetTheme(_originalTheme, persist: false);
        Loc.SetLanguage(_originalLanguage, persist: false);
        _saved = true; // already restored — OnClosing must not restore twice
        DialogResult = false;
        Close();
    }

    private bool _saved;

    /// <summary>Closing via the title-bar X behaves like Cancel: previews (color pack /
    /// language) applied since opening are reverted, so nothing silently leaks.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_saved)
        {
            ThemeManager.Current.SetTheme(_originalTheme, persist: false);
            Loc.SetLanguage(_originalLanguage, persist: false);
        }
        base.OnClosing(e);
    }
}
