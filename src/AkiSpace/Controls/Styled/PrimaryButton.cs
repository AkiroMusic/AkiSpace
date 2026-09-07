using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Primary button - accent background, accent text, shadow-accent, spring transition.
/// </summary>
public sealed class PrimaryButton : Button
{
    private bool _hovered = false;
    private bool _pressed = false;
    private float _animProgress = 0f;
    private readonly System.Windows.Forms.Timer _timer;

    public PrimaryButton()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = Color.Transparent; // We paint text ourselves
        Font = ThemeManager.Current.GetFontSans(15f, FontStyle.Bold);
        Size = new Size(120, 40);
        Cursor = Cursors.Hand;

        _timer = new Timer { Interval = 16 };
        _timer.Tick += (_, _) => Animate();

        MouseEnter += (_, _) => { _hovered = true; _timer.Start(); };
        MouseLeave += (_, _) => { _hovered = false; _timer.Start(); };
        MouseDown += (_, _) => { _pressed = true; Invalidate(); };
        MouseUp += (_, _) => { _pressed = false; Invalidate(); };
    }

    private void Animate()
    {
        var (easing, duration) = ThemeManager.Current.GetMotion("spring");
        var target = _hovered || _pressed ? 1f : 0f;
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

        // Interpolate colors
        var bgColor = InterpolateColor(ThemeManager.Current.Surface2, ThemeManager.Current.Accent, _animProgress * 0.12f);
        var borderColor = InterpolateColor(ThemeManager.Current.Border, ThemeManager.Current.Accent, _animProgress);
        var textColor = InterpolateColor(ThemeManager.Current.TextPrimary, ThemeManager.Current.Accent, _animProgress);
        var shadowColors = InterpolateShadows(ThemeManager.Current.Shadow1, ThemeManager.Current.ShadowAccent, _animProgress);

        // Shadow
        DrawShadow(g, rect, shadowColors);

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

    private void DrawShadow(Graphics g, Rectangle rect, Color[] colors)
    {
        if (colors.Length < 2) return;
        var shadowRect = new Rectangle(rect.X, rect.Y + 1, rect.Width, rect.Height);
        using var brush = new SolidBrush(Color.FromArgb(colors[0].A, colors[0]));
        using var path = GetRoundedRect(shadowRect, ThemeManager.Current.Radius("sm"));
        for (int i = 1; i <= 2; i++)
        {
            var r = new Rectangle(shadowRect.X, shadowRect.Y + i, shadowRect.Width, shadowRect.Height);
            using var p = GetRoundedRect(r, ThemeManager.Current.Radius("sm"));
            g.FillPath(brush, p);
        }
        var diffuseRect = new Rectangle(rect.X - 4, rect.Y + 4, rect.Width + 8, rect.Height + 8);
        using var diffuseBrush = new SolidBrush(Color.FromArgb(colors[1].A, colors[1]));
        for (int i = 4; i <= 16; i += 4)
        {
            var r = new Rectangle(diffuseRect.X, diffuseRect.Y + i, diffuseRect.Width, diffuseRect.Height);
            using var p = GetRoundedRect(r, ThemeManager.Current.Radius("sm"));
            g.FillPath(diffuseBrush, p);
        }
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

    private Color[] InterpolateShadows(Color[] from, Color[] to, float t)
    {
        var result = new Color[Math.Max(from.Length, to.Length)];
        for (int i = 0; i < result.Length; i++)
        {
            var f = i < from.Length ? from[i] : from[^1];
            var tt = i < to.Length ? to[i] : to[^1];
            result[i] = InterpolateColor(f, tt, t);
        }
        return result;
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