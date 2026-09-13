using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Styled ListView - surface-1 background, custom row painting, accent selection, hover states.
/// </summary>
public sealed class StyledListView : ListView
{
    private readonly System.Windows.Forms.Timer _timer;
    private int _hoveredIndex = -1;

    public StyledListView()
    {
        // Deliberately NOT UserPaint/AllPaintingInWmPaint: those styles suppress the
        // native ListView paint path, so NM_CUSTOMDRAW never fires and OwnerDraw's
        // OnDrawItem/OnDrawColumnHeader render NOTHING (an empty control). OwnerDraw
        // alone is the supported custom-paint path; DoubleBuffered kills the flicker.
        DoubleBuffered = true;
        View = View.Details;
        FullRowSelect = true;
        GridLines = false;
        BorderStyle = BorderStyle.None;
        // Solid color: the native control has no transparent-backcolor compositing,
        // and this must match the row background painted in OnDrawItem.
        BackColor = ThemeManager.Current.Surface1;
        ForeColor = ThemeManager.Current.TextPrimary;
        Font = ThemeManager.Current.GetFontSans(13f, FontStyle.Regular);
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        OwnerDraw = true;

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => Invalidate();

        MouseMove += (_, e) =>
        {
            var item = GetItemAt(e.X, e.Y);
            var newIndex = item?.Index ?? -1;
            if (newIndex != _hoveredIndex)
            {
                _hoveredIndex = newIndex;
                Invalidate();
            }
        };
        MouseLeave += (_, _) => { _hoveredIndex = -1; Invalidate(); };
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var header = e.Header;
        if (header is null) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = e.Bounds;
        using var bgBrush = new SolidBrush(ThemeManager.Current.Surface1);
        g.FillRectangle(bgBrush, rect);

        // Bottom border
        using var borderPen = new Pen(ThemeManager.Current.Border, 1);
        g.DrawLine(borderPen, rect.Left, rect.Bottom - 1, rect.Right, rect.Bottom - 1);

        var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        using var textBrush = new SolidBrush(ThemeManager.Current.TextTertiary);
        var textRect = new RectangleF(rect.X + 16, rect.Y, rect.Width - 32, rect.Height);
        g.DrawString(header.Text, ThemeManager.Current.GetFontSans(10f, FontStyle.Bold), textBrush, textRect, sf);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        // Cells are painted per-column in OnDrawSubItem. Drawing the whole row here
        // TOO double-draws everything (the row-overlap garbling) — stay a no-op.
        e.DrawDefault = false;
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = e.Bounds;
        var item = e.Item;
        if (item is null) return;
        var isSelected = item.Selected;
        var isHovered = item.Index == _hoveredIndex && !isSelected;

        // Cell background
        Color rowBg;
        if (isSelected)
            rowBg = Color.FromArgb(10, ThemeManager.Current.Accent.R, ThemeManager.Current.Accent.G, ThemeManager.Current.Accent.B); // 4%
        else if (isHovered)
            rowBg = Color.FromArgb(6, ThemeManager.Current.TextPrimary.R, ThemeManager.Current.TextPrimary.G, ThemeManager.Current.TextPrimary.B); // ~2.4%
        else
            rowBg = ThemeManager.Current.Surface1;

        using (var bgBrush = new SolidBrush(rowBg))
        {
            g.FillRectangle(bgBrush, rect);
        }

        // Row separator
        using (var sepPen = new Pen(ThemeManager.Current.Border, 1))
        {
            g.DrawLine(sepPen, rect.Left, rect.Bottom - 1, rect.Right, rect.Bottom - 1);
        }

        // Selection accent indicator (left edge of the first column)
        if (isSelected && e.ColumnIndex == 0)
        {
            using var accentBrush = new SolidBrush(ThemeManager.Current.Accent);
            g.FillRectangle(accentBrush, rect.Left, rect.Top, 3, rect.Height);
        }

        var subItem = e.SubItem;
        if (subItem is null) return;

        var textColor = isSelected
            ? ThemeManager.Current.Accent
            : (e.ColumnIndex == 0 ? ThemeManager.Current.TextPrimary : ThemeManager.Current.TextSecondary);

        var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        // Long details must not wrap: wrapped lines vertically overflow the ~20px
        // row and bleed across neighbouring rows as unreadable "ghost" text.
        sf.FormatFlags |= StringFormatFlags.NoWrap;
        using var textBrush = new SolidBrush(textColor);
        var textRect = new RectangleF(rect.X + 16, rect.Y, Math.Max(0, rect.Width - 32), rect.Height);
        g.DrawString(subItem.Text, Font, textBrush, textRect, sf);

        e.DrawDefault = false;
    }
}