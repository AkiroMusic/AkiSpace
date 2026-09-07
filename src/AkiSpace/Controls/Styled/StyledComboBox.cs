using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Styled ComboBox - surface-2 background, border, accent focus, custom dropdown.
/// </summary>
public sealed class StyledComboBox : ComboBox
{
    private bool _focused = false;
    private bool _dropped = false;
    private float _focusProgress = 0f;
    private readonly System.Windows.Forms.Timer _timer;

    public StyledComboBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        BackColor = Color.Transparent;
        Font = ThemeManager.Current.GetFontSans(13f, FontStyle.Regular);
        ForeColor = ThemeManager.Current.TextPrimary;
        Size = new Size(200, 40);
        ItemHeight = 36;

        _timer = new Timer { Interval = 16 };
        _timer.Tick += (_, _) => Animate();

        Enter += (_, _) => { _focused = true; _timer.Start(); };
        Leave += (_, _) => { _focused = false; _dropped = false; _timer.Start(); };
        DropDown += (_, _) => { _dropped = true; Invalidate(); };
        DropDownClosed += (_, _) => { _dropped = false; Invalidate(); };
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

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = e.Bounds;
        var isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var isFocused = (e.State & DrawItemState.Focus) == DrawItemState.Focus;
        var isHover = isSelected || isFocused;

        var bgColor = isHover
            ? Color.FromArgb(26, ThemeManager.Current.Accent.R, ThemeManager.Current.Accent.G, ThemeManager.Current.Accent.B) // 10%
            : ThemeManager.Current.Surface2;
        var textColor = isHover ? ThemeManager.Current.Accent : ThemeManager.Current.TextSecondary;
        var borderColor = isHover ? ThemeManager.Current.Accent : ThemeManager.Current.Border;

        using var bgBrush = new SolidBrush(bgColor);
        g.FillRectangle(bgBrush, rect);

        if (isHover)
        {
            using var borderPen = new Pen(borderColor, 1);
            g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        var text = Items[e.Index]?.ToString() ?? "";
        var textRect = new RectangleF(rect.X + 16, rect.Y, rect.Width - 32, rect.Height);
        var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        using var textBrush = new SolidBrush(textColor);
        g.DrawString(text, Font, textBrush, textRect, sf);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = ThemeManager.Current.Radius("sm");

        var bgColor = ThemeManager.Current.Surface2;
        var borderColor = InterpolateColor(ThemeManager.Current.Border, ThemeManager.Current.Accent, _focusProgress);

        using var bgBrush = new SolidBrush(bgColor);
        using var path = GetRoundedRect(rect, radius);
        g.FillPath(bgBrush, path);

        using var borderPen = new Pen(borderColor, 1);
        g.DrawPath(borderPen, path);

        // Text
        var text = SelectedItem?.ToString() ?? "";
        var textRect = new RectangleF(16, 0, Width - 48, Height);
        var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        using var textBrush = new SolidBrush(ThemeManager.Current.TextPrimary);
        g.DrawString(text, Font, textBrush, textRect, sf);

        // Dropdown arrow
        var arrowColor = _focused ? ThemeManager.Current.Accent : ThemeManager.Current.TextTertiary;
        var arrowX = Width - 28;
        var arrowY = Height / 2;
        using var arrowBrush = new SolidBrush(arrowColor);
        var arrowPoints = new[]
        {
            new PointF(arrowX - 5, arrowY - 2),
            new PointF(arrowX + 5, arrowY - 2),
            new PointF(arrowX, arrowY + 3)
        };
        g.FillPolygon(arrowBrush, arrowPoints);
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