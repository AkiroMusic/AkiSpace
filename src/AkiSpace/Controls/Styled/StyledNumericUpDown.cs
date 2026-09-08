using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Styled NumericUpDown - surface-2 background, border, accent focus, mono font for numbers.
/// </summary>
public sealed class StyledNumericUpDown : NumericUpDown
{
    private bool _focused = false;
    private float _focusProgress = 0f;
    private readonly System.Windows.Forms.Timer _timer;

    public StyledNumericUpDown()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BorderStyle = BorderStyle.None;
        BackColor = Color.Transparent;
        Font = ThemeManager.Current.GetFontMono(20f, FontStyle.Bold);
        ForeColor = ThemeManager.Current.TextPrimary;
        TextAlign = HorizontalAlignment.Center;
        Size = new Size(100, 44);
        Minimum = 0;
        Maximum = 10000;
        Increment = 1;

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => Animate();

        Enter += (_, _) => { _focused = true; _timer.Start(); };
        Leave += (_, _) => { _focused = false; _timer.Start(); };
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

        using var bgBrush = new SolidBrush(bgColor);
        using var path = DrawHelpers.GetRoundedRect(rect, radius);
        g.FillPath(bgBrush, path);

        using var borderPen = new Pen(borderColor, 1);
        g.DrawPath(borderPen, path);

        // Draw up/down buttons
        var btnWidth = 28;
        var btnRectUp = new Rectangle(Width - btnWidth, 0, btnWidth, Height / 2);
        var btnRectDown = new Rectangle(Width - btnWidth, Height / 2, btnWidth, Height - Height / 2);

        var btnColor = _focused ? ThemeManager.Current.Accent : ThemeManager.Current.TextTertiary;
        using var btnBrush = new SolidBrush(btnColor);

        // Up arrow
        var cx = btnRectUp.X + btnRectUp.Width / 2;
        var cy = btnRectUp.Y + btnRectUp.Height / 2;
        var s = 4f;
        g.FillPolygon(btnBrush, new[]
        {
            new PointF(cx, cy - s),
            new PointF(cx - s, cy + s * 0.5f),
            new PointF(cx + s, cy + s * 0.5f)
        });

        // Down arrow
        cy = btnRectDown.Y + btnRectDown.Height / 2;
        g.FillPolygon(btnBrush, new[]
        {
            new PointF(cx, cy + s),
            new PointF(cx - s, cy - s * 0.5f),
            new PointF(cx + s, cy - s * 0.5f)
        });

        // Text (use base for editing support)
        var textRect = new RectangleF(12, 0, Width - btnWidth - 24, Height);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var textBrush = new SolidBrush(ForeColor);
        g.DrawString(Text, Font, textBrush, textRect, sf);
    }
}