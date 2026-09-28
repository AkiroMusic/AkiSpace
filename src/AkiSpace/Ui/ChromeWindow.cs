using System.Windows;
using System.Windows.Interop;
using AkiSpace.Common;
using AkiSpace.Native;

namespace AkiSpace.Ui;

/// <summary>
/// WPF window whose DWM titlebar follows the active color pack: dark packs get
/// the immersive-dark titlebar, light packs the default light one (parity with
/// the WinForms forms, which swap chrome on ThemeManager.Current.ThemeChanged).
/// All AkiSpace windows derive from this.
/// </summary>
public class ChromeWindow : Window
{
    public ChromeWindow()
    {
        SourceInitialized += (_, _) => ApplyThemeTitleBar();
        ThemeManager.Current.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Current.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, string theme) => ApplyThemeTitleBar();

    private void ApplyThemeTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = ThemeManager.Current.Palette.LightTheme ? 0 : 1;
            User32.DwmSetWindowAttribute(hwnd, User32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // Older DWM without attribute 20 — default chrome is acceptable.
        }
    }
}
