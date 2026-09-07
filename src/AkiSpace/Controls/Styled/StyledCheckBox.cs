using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Styled CheckBox - custom checkmark, accent color when checked, smooth transitions.
/// </summary>
public sealed class StyledCheckBox : CheckBox
{
    private bool _hovered = false;
    private float _checkProgress = 0f;
    private readonly System.Windows.Forms.Timer _timer;

    public StyledCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Font = ThemeManager.Current.GetFontSans(13f, FontStyle.Regular);
        ForeColor = ThemeManager.Current.TextPrimary;
        AutoSize = true;
        Cursor = Cursors.Hand;

        _timer = new Timer { Interval = 16 };
        _timer.Tick += (_, _) => Animate();

        MouseEnter += (_, _) => { _hovered = true; _timer.Start(); };
        MouseLeave += (_, _) => { _hovered = false; _timer.Start(); };
        CheckedChanged += (_, _) => _timer.Start();
    }

    private void Animate()
    {
        var (easing, duration) = ThemeManager.Current.GetMotion("hover");
        var target = Checked ? 1f : 0f;
        var speed = 1f / (duration / 16f);

        if (_checkProgress < target)
            _checkProgress = Math.Min(target, _checkProgress + speed);
        else if (_checkProgress > target)
            _checkProgress = Math.Max(target, _checkProgress - speed);
        else
            _timer.Stop();

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        const int boxSize = 20;
        var boxRect = new Rectangle(0, (Height - boxSize) / 2, boxSize, boxSize);
        var radius = ThemeManager.Current.Radius("sm");

        // Box background
        var boxBg = Checked
            ? InterpolateColor(ThemeManager.Current.Surface2, Color.FromArgb(26, ThemeManager.Current.Accent.R, ThemeManager.Current.Accent.G, ThemeManager.Current.Accent.B), _checkProgress)
            : ThemeManager.Current.Surface2;
        var boxBorder = Checked
            ? InterpolateColor(ThemeManager.Current.Border, ThemeManager.Current.Accent, _checkProgress)
            : InterpolateColor(ThemeManager.Current.Border, ThemeManager.Current.TextTertiary, _hovered ? 1f : 0f);

        using var bgBrush = new SolidBrush(boxBg);
        using var path = GetRoundedRect(boxRect, radius);
        g.FillPath(bgBrush, path);

        using var borderPen = new Pen(boxBorder, 1);
        g.DrawPath(borderPen, path);

        // Checkmark
        if (_checkProgress > 0.1f)
        {
            var checkColor = InterpolateColor(Color.Transparent, ThemeManager.Current.Accent, _checkProgress);
            using var checkPen = new Pen(checkColor, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var cx = boxRect.X + boxRect.Width / 2;
            var cy = boxRect.Y + boxRect.Height / 2;
            var s = boxSize * 0.35f;
            g.DrawLines(checkPen, new[]
            {
                new PointF(cx - s, cy),
                new PointF(cx - s * 0.3f, cy + s * 0.7f),
                new PointF(cx + s * 0.8f, cy - s * 0.8f)
            });
        }

        // Text
        if (!string.IsNullOrEmpty(Text))
        {
            var textRect = new RectangleF(boxRect.Right + 10, 0, Width - boxRect.Right - 10, Height);
            var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            using var textBrush = new SolidBrush(ThemeManager.Current.TextPrimary);
            g.DrawString(Text, Font, textBrush, textRect, sf);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var textSize = TextRenderer.MeasureText(Text, Font);
        return new Size(30 + textSize.Width, Math.Max(24, textSize.Height));
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