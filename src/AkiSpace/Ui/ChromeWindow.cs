using System.Windows;
using System.Windows.Interop;
using AkiSpace.Native;

namespace AkiSpace.Ui;

/// <summary>
/// WPF window with the DWM immersive-dark titlebar applied (parity with the
/// WinForms forms). All AkiSpace windows derive from this.
/// </summary>
public class ChromeWindow : Window
{
    public ChromeWindow()
    {
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
    }

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int value = 1;
            User32.DwmSetWindowAttribute(hwnd, User32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // Older DWM without attribute 20 — default chrome is acceptable.
        }
    }
}
