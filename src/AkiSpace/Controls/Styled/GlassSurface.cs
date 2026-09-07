using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Glass surface panel with backdrop blur effect (via DWM on Windows 10+).
/// Used for titlebar, footer, floating toolbars.
/// </summary>
public sealed class GlassSurface : Panel
{
    private bool _darkModeApplied = false;

    public GlassSurface()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        Padding = new Padding(0);
        Margin = new Padding(0);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkMode();
        ApplyBlur();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var brush = new SolidBrush(ThemeManager.Current.GlassBg);
        using var pen = new Pen(ThemeManager.Current.GlassBorder, 1);

        // Draw rounded background
        var radius = ThemeManager.Current.Radius("sm");
        using var path = GetRoundedRect(rect, radius);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    private void ApplyDarkMode()
    {
        if (_darkModeApplied) return;
        try
        {
            var value = 1;
            AkiSpace.Native.User32.DwmSetWindowAttribute(Handle, AkiSpace.Native.User32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
            _darkModeApplied = true;
        }
        catch { /* DWM not available */ }
    }

    private void ApplyBlur()
    {
        try
        {
            var accent = new AkiSpace.Native.User32.ACCENT_POLICY
            {
                AccentState = AkiSpace.Native.User32.ACCENT_STATE.ACCENT_ENABLE_BLURBEHIND,
                GradientColor = 0
            };
            var accentStructSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentStructSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new AkiSpace.Native.User32.WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = AkiSpace.Native.User32.WINDOWCOMPOSITIONATTRIB.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentStructSize
            };

            AkiSpace.Native.User32.SetWindowCompositionAttribute(Handle, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }
        catch { /* Blur not supported */ }
    }

    private static GraphicsPath GetRoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}