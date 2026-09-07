using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Ghost/Secondary button - transparent background, border, text color transition.
/// </summary>
public sealed class GhostButton : Button
{
    private bool _hovered = false;
    private float _animProgress = 0f;
    private readonly System.Windows.Forms.Timer _timer;

    public GhostButton()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = Color.Transparent;
        Font = ThemeManager.Current.GetFontSans(12f, FontStyle.Regular);
        Size = new Size(100, 36);
        Cursor = Cursors.Hand;

        _timer = new Timer { Interval = 16 };
        _timer.Tick += (_, _) => Animate();

        MouseEnter += (_, _) => { _hovered = true; _timer.Start(); };
        MouseLeave += (_, _) => { _hovered = false; _timer.Start(); };
    }

    private void Animate()
    {
        var (easing, duration) = ThemeManager.Current.GetMotion("hover");
        var target = _hovered ? 1f : 0f;
        var speed = 1f / (duration / 16f);

        if (_animProgress < target)
            _animProgress = Math.Min(target, _animProgress + speed);
        else if (_animProgress > target)
            _animProgress = Math.Max(target, _animProgress - speed);
        else
            _timer.Stop();

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = ThemeManager.Current.Radius("sm");

        var borderColor = InterpolateColor(ThemeManager.Current.Border, ThemeManager.Current.TextTertiary, _animProgress);
        var textColor = InterpolateColor(ThemeManager.Current.TextTertiary, ThemeManager.Current.TextSecondary, _animProgress);
        var bgColor = InterpolateColor(Color.Transparent, Color.FromArgb(15, ThemeManager.Current.TextPrimary.R, ThemeManager.Current.TextPrimary.G, ThemeManager.Current.TextPrimary.B), _animProgress);

        // Background
        using var bgBrush = new SolidBrush(bgColor);
        using var path = GetRoundedRect(rect, radius);
        g.FillPath(bgBrush, path);

        // Border
        using var borderPen = new Pen(borderColor, 1);
        g.DrawPath(borderPen, path);

        // Text
        var textRect = new RectangleF(0, 0, Width, Height);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var textBrush = new SolidBrush(textColor);
        g.DrawString(Text, Font, textBrush, textRect, sf);
    }

    private Color InterpolateColor(Color from, Color to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(from.A + (to.A - from.A) * t),
            (int)(from.R + (to.R - from.R) * t),
            (int)(from.G + (to.G - from.G) * t),
            (int)(from.B + (to.B - from.B) * t)
        );
    }

    private static GraphicsPath GetRoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d > rect.Width) d = rect.Width;
        if (d > rect.Height) d = rect.Height;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}