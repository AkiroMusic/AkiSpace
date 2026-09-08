using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Navigation rail - 80px wide, icon+label pills, active=accent background.
/// Google/Material 3 style rail with 21px icons, 10px labels.
/// </summary>
public sealed class NavigationRail : Panel
{
    private readonly FlowLayoutPanel _contentPanel = new();
    private readonly List<NavItem> _items = new();
    private int _selectedIndex = -1;
    private string? _activeGroup = null;

    public NavigationRail()
    {
        Width = 80;
        Dock = DockStyle.Left;
        BackColor = ThemeManager.Current.Surface1;
        Padding = new Padding(0, 8, 0, 8);

        _contentPanel.Dock = DockStyle.Fill;
        _contentPanel.FlowDirection = FlowDirection.TopDown;
        _contentPanel.WrapContents = false;
        _contentPanel.AutoScroll = true;
        _contentPanel.BackColor = Color.Transparent;
        _contentPanel.Padding = new Padding(0);

        // Hide scrollbar
        typeof(Panel).InvokeMember("SetStyle",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.InvokeMethod,
            null, _contentPanel, new object[] { ControlStyles.UserPaint, true });

        Controls.Add(_contentPanel);

        // Right border
        Paint += (_, e) =>
        {
            using var pen = new Pen(ThemeManager.Current.Border, 1);
            e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        };
    }

    public void AddItem(string group, string iconSvg, string label, Action onClick)
    {
        var item = new NavItem(group, iconSvg, label, onClick);
        _items.Add(item);
        RebuildItems();
    }

    public void SetSelected(string group, string label)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Group == group && _items[i].Label == label)
            {
                _selectedIndex = i;
                _activeGroup = group;
                break;
            }
        }
        RebuildItems();
    }

    private void RebuildItems()
    {
        _contentPanel.Controls.Clear();

        string? lastGroup = null;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];

            // Group separator
            if (item.Group != lastGroup && lastGroup != null)
            {
                var separator = new Panel
                {
                    Height = 1,
                    Margin = new Padding(16, 8, 16, 8),
                    BackColor = Color.FromArgb(150, ThemeManager.Current.Border.R, ThemeManager.Current.Border.G, ThemeManager.Current.Border.B),
                };
                _contentPanel.Controls.Add(separator);
            }

            var btn = CreateNavButton(item, i);
            _contentPanel.Controls.Add(btn);

            lastGroup = item.Group;
        }
    }

    private Button CreateNavButton(NavItem item, int index)
    {
        var isSelected = index == _selectedIndex;
        var btn = new Button
        {
            Width = 64, // 100% - 16px margins
            Height = 52,
            Margin = new Padding(8, 1, 8, 1),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = isSelected
                ? Color.FromArgb(33, ThemeManager.Current.Accent.R, ThemeManager.Current.Accent.G, ThemeManager.Current.Accent.B) // 13%
                : Color.Transparent,
            ForeColor = isSelected ? ThemeManager.Current.Accent : ThemeManager.Current.TextTertiary,
            Text = "",
            Cursor = Cursors.Hand,
            Tag = index,
        };

        btn.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var rect = new Rectangle(0, 0, btn.Width - 1, btn.Height - 1);
            var radius = 14; // 14px as per spec

            // Background (handled by BackColor, but we repaint for hover)
            using var bgBrush = new SolidBrush(btn.BackColor);
            using var path = DrawHelpers.GetRoundedRect(rect, radius);
            g.FillPath(bgBrush, path);

            // Icon (21px)
            var iconRect = new RectangleF(
                (btn.Width - 21) / 2f,
                8,
                21, 21
            );
            DrawSvgIcon(g, item.IconSvg, iconRect, btn.ForeColor);

            // Label (10px, letter-spacing 0.02em, max-width 62px)
            var labelFont = ThemeManager.Current.GetFontSans(10f, FontStyle.Regular);
            var labelColor = btn.ForeColor;
            var labelRect = new RectangleF(0, 32, btn.Width, 18);
            var sf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            using var labelBrush = new SolidBrush(labelColor);
            g.DrawString(item.Label, labelFont, labelBrush, labelRect, sf);
        };

        btn.Click += (_, _) => item.OnClick?.Invoke();
        btn.MouseEnter += (_, _) =>
        {
            if (!isSelected)
            {
                btn.BackColor = Color.FromArgb(15, ThemeManager.Current.TextPrimary.R, ThemeManager.Current.TextPrimary.G, ThemeManager.Current.TextPrimary.B); // 6%
                btn.ForeColor = ThemeManager.Current.TextSecondary;
            }
        };
        btn.MouseLeave += (_, _) =>
        {
            if (!isSelected)
            {
                btn.BackColor = Color.Transparent;
                btn.ForeColor = ThemeManager.Current.TextTertiary;
            }
        };

        return btn;
    }

    private void DrawSvgIcon(Graphics g, string svgPath, RectangleF rect, Color color)
    {
        // Simplified: draw a placeholder icon using graphics primitives
        // In production, parse SVG path data. For now, draw a simple shape based on icon name.
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var s = rect.Width * 0.4f;

        // Simple icon mapping
        if (svgPath.Contains("home") || svgPath.Contains("monitor"))
        {
            // Monitor icon
            g.DrawRectangle(pen, cx - s, cy - s * 0.7f, s * 2, s * 1.4f);
            g.DrawLine(pen, cx - s * 0.5f, cy + s * 0.7f, cx + s * 0.5f, cy + s * 0.7f);
        }
        else if (svgPath.Contains("settings") || svgPath.Contains("gear"))
        {
            // Gear icon
            g.DrawEllipse(pen, cx - s * 0.5f, cy - s * 0.5f, s, s);
            for (int i = 0; i < 8; i++)
            {
                var angle = i * Math.PI / 4;
                var x1 = cx + (float)Math.Cos(angle) * s * 0.7f;
                var y1 = cy + (float)Math.Sin(angle) * s * 0.7f;
                var x2 = cx + (float)Math.Cos(angle) * s * 1.1f;
                var y2 = cy + (float)Math.Sin(angle) * s * 1.1f;
                g.DrawLine(pen, x1, y1, x2, y2);
            }
        }
        else if (svgPath.Contains("plug") || svgPath.Contains("connect"))
        {
            // Plug/connect icon
            g.DrawLine(pen, cx - s, cy, cx + s, cy);
            g.DrawLine(pen, cx, cy - s, cx, cy + s);
            g.DrawEllipse(pen, cx - s * 0.4f, cy - s * 0.4f, s * 0.8f, s * 0.8f);
        }
        else
        {
            // Default: circle
            g.DrawEllipse(pen, cx - s, cy - s, s * 2, s * 2);
        }
    }

    private sealed class NavItem
    {
        public string Group { get; }
        public string IconSvg { get; }
        public string Label { get; }
        public Action? OnClick { get; }

        public NavItem(string group, string iconSvg, string label, Action onClick)
        {
            Group = group;
            IconSvg = iconSvg;
            Label = label;
            OnClick = onClick;
        }
    }
}