using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Ambient background panel - renders radial gradient glows matching the current theme.
/// Covers the entire form behind all content.
/// </summary>
public sealed class AmbientBackground : Panel
{
    public AmbientBackground()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Dock = DockStyle.Fill;
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Base background
        using var baseBrush = new SolidBrush(ThemeManager.Current.BgBase);
        g.FillRectangle(baseBrush, ClientRectangle);

        // Radial gradient glows
        var glows = ThemeManager.Current.AmbientGlows;
        foreach (var (rectF, color) in glows)
        {
            var rect = new RectangleF(
                ClientRectangle.Width * rectF.X,
                ClientRectangle.Height * rectF.Y,
                ClientRectangle.Width * rectF.Width,
                ClientRectangle.Height * rectF.Height
            );

            using var path = new GraphicsPath();
            path.AddEllipse(rect);
            using var brush = new PathGradientBrush(path)
            {
                CenterColor = color,
                SurroundColors = new[] { Color.FromArgb(0, color.R, color.G, color.B) },
                FocusScales = new PointF(0.5f, 0.5f)
            };
            g.FillEllipse(brush, rect);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }
}