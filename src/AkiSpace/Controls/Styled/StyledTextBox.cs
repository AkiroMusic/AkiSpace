using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Styled TextBox - surface-2 background, border, accent focus border, mono font for numbers.
/// </summary>
public sealed class StyledTextBox : TextBox
{
    private bool _focused = false;
    private float _focusProgress = 0f;
    private readonly System.Windows.Forms.Timer _timer;

    public StyledTextBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BorderStyle = BorderStyle.None;
        BackColor = Color.Transparent;
        Font = ThemeManager.Current.GetFontSans(13f, FontStyle.Regular);
        ForeColor = ThemeManager.Current.TextPrimary;
        Padding = new Padding(12, 8, 12, 8);
        Size = new Size(200, 40);

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => Animate();

        Enter += (_, _) => { _focused = true; _timer.Start(); };
        Leave += (_, _) => { _focused = false; _timer.Start(); };
    }

    public void SetMonoFont()
    {
        Font = ThemeManager.Current.GetFontMono(20f, FontStyle.Bold);
    }

    private void Animate()
    {
        var (easing, duration) = ThemeManager.Current.GetMotion("hover");
        var target = _focused ? 1f : 0f;
        var speed = 1f / (duration / 16f);

        if (_focusProgress < target)
            _focusProgress = Math.Min(target, _focusProgress + speed);
        else if (_focusProgress > target)
            _focusProgress = Math.Max(target, _focusProgress - speed);
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

        var bgColor = ThemeManager.Current.Surface2;
        var borderColor = DrawHelpers.InterpolateColor(ThemeManager.Current.Border, ThemeManager.Current.Accent, _focusProgress);

        // Background
        using var bgBrush = new SolidBrush(bgColor);
        using var path = DrawHelpers.GetRoundedRect(rect, radius);
        g.FillPath(bgBrush, path);

        // Border
        using var borderPen = new Pen(borderColor, 1);
        g.DrawPath(borderPen, path);

        // Text (use base painting for text rendering with selection support)
        var textRect = new RectangleF(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(textRect), ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}