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
        var bgColor = DrawHelpers.InterpolateColor(ThemeManager.Current.Surface2, ThemeManager.Current.Accent, _animProgress * 0.12f);
        var borderColor = DrawHelpers.InterpolateColor(ThemeManager.Current.Border, ThemeManager.Current.Accent, _animProgress);
        var textColor = DrawHelpers.InterpolateColor(ThemeManager.Current.TextPrimary, ThemeManager.Current.Accent, _animProgress);
        var shadowColors = DrawHelpers.InterpolateShadows(ThemeManager.Current.Shadow1, ThemeManager.Current.ShadowAccent, _animProgress);

        // Shadow
        DrawHelpers.DrawShadow(g, rect, shadowColors, ThemeManager.Current.Radius("sm"));

        // Background
        using var bgBrush = new SolidBrush(bgColor);
        using var path = DrawHelpers.GetRoundedRect(rect, radius);
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
}