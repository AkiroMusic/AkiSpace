using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Double-bezel card - the primary container for all tool panels.
/// Features: outer border + 5px inner inset border, spring hover lift, accent border on hover.
/// </summary>
public sealed class DoubleBezelCard : Panel
{
    private bool _hovered = false;
    private float _hoverProgress = 0f; // 0 to 1
    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly int _cornerRadius = ThemeTokens.Radius.Lg; // 24px
    private readonly int _insetOffset = 5; // 5px inner bezel

    // Header section
    private readonly Panel _headerPanel = new();
    private readonly Label _titleLabel = new();
    private readonly Label _subtitleLabel = new();
    private readonly Panel _headerActionPanel = new();
    private Control? _headerActionControl;
    private bool _hasSubtitle = false;

    public DoubleBezelCard()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Padding = new Padding(ThemeTokens.Space.S6); // 24px
        Margin = new Padding(0, 0, 0, ThemeTokens.Space.S6); // margin-bottom 24px

        // Animation timer for spring hover
        _animationTimer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps
        _animationTimer.Tick += (_, _) => AnimateHover();

        // Header setup
        SetupHeader();

        // Events
        MouseEnter += (_, _) => SetHovered(true);
        MouseLeave += (_, _) => SetHovered(false);
    }

    private void SetupHeader()
    {
        _headerPanel.Dock = DockStyle.Top;
        _headerPanel.AutoSize = true;
        _headerPanel.BackColor = Color.Transparent;
        _headerPanel.Padding = new Padding(0, 0, 0, ThemeTokens.Space.S2); // 8px default, adjusted in UpdateHeader

        var headerLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
        };

        var titleContainer = new Panel
        {
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        var titleLayout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
        };

        // Title: Fraunces 20px 600, line-height 1.25, letter-spacing -0.01em
        _titleLabel.AutoSize = true;
        _titleLabel.Font = ThemeManager.Current.GetFontDisplay(ThemeTokens.Typography.CardTitle, FontStyle.Bold);
        _titleLabel.ForeColor = ThemeManager.Current.TextPrimary;
        _titleLabel.Margin = new Padding(0, 0, 0, 4);
        _titleLabel.BackColor = Color.Transparent;

        // Subtitle: 12px tertiary, line-height 1.5
        _subtitleLabel.AutoSize = true;
        _subtitleLabel.Font = ThemeManager.Current.GetFontSans(ThemeTokens.Typography.CardSubtitle, FontStyle.Regular);
        _subtitleLabel.ForeColor = ThemeManager.Current.TextTertiary;
        _subtitleLabel.BackColor = Color.Transparent;
        _subtitleLabel.Visible = false;

        titleLayout.Controls.Add(_titleLabel);
        titleLayout.Controls.Add(_subtitleLabel);
        titleContainer.Controls.Add(titleLayout);

        _headerActionPanel.AutoSize = true;
        _headerActionPanel.BackColor = Color.Transparent;
        _headerActionPanel.Dock = DockStyle.Right;

        headerLayout.Controls.Add(titleContainer);
        headerLayout.Controls.Add(_headerActionPanel);
        headerLayout.SetFlowBreak(titleContainer, false);

        _headerPanel.Controls.Add(headerLayout);
        Controls.Add(_headerPanel);
    }

    /// <summary>Card title (Fraunces 20px 600)</summary>
    public string Title
    {
        get => _titleLabel.Text;
        set => _titleLabel.Text = value;
    }

    /// <summary>Card subtitle (12px tertiary)</summary>
    public string Subtitle
    {
        get => _subtitleLabel.Text;
        set
        {
            _subtitleLabel.Text = value;
            _hasSubtitle = !string.IsNullOrEmpty(value);
            _subtitleLabel.Visible = _hasSubtitle;
            UpdateHeaderSpacing();
        }
    }

    /// <summary>Right-aligned action in header (e.g., reset button)</summary>
    public Control? HeaderAction
    {
        get => _headerActionControl;
        set
        {
            if (_headerActionControl != null)
                _headerActionPanel.Controls.Remove(_headerActionControl);
            _headerActionControl = value;
            if (value != null)
            {
                value.Margin = new Padding(ThemeTokens.Space.S4, 0, 0, 0); // gap-4
                _headerActionPanel.Controls.Add(value);
            }
        }
    }

    /// <summary>Card content (replaces Children)</summary>
    public new ControlCollection ContentControls => Controls;

    private void UpdateHeaderSpacing()
    {
        _headerPanel.Padding = new Padding(0, 0, 0, _hasSubtitle ? ThemeTokens.Space.S2 : ThemeTokens.Space.S6);
    }

    private void SetHovered(bool hovered)
    {
        _hovered = hovered;
        if (!_animationTimer.Enabled)
            _animationTimer.Start();
    }

    private void AnimateHover()
    {
        var (easing, duration) = ThemeManager.Current.GetMotion("spring");
        var target = _hovered ? 1f : 0f;
        var speed = 1f / (duration / 16f); // per frame

        if (_hovered)
            _hoverProgress = Math.Min(1f, _hoverProgress + speed);
        else
            _hoverProgress = Math.Max(0f, _hoverProgress - speed);

        Invalidate();

        if (_hoverProgress == target)
            _animationTimer.Stop();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        // Interpolate hover transform (translateY -1px at full progress)
        var translateY = (int)(-_hoverProgress * 1f);

        // Outer border color interpolation
        var borderColor = DrawHelpers.InterpolateColor(ThemeManager.Current.Border, ThemeManager.Current.Accent, _hoverProgress);

        // Shadow interpolation
        var shadowColors = DrawHelpers.InterpolateShadows(ThemeManager.Current.Shadow1, ThemeManager.Current.ShadowAccent, _hoverProgress);

        // Draw shadow (dual-layer)
        DrawHelpers.DrawShadow(g, rect, shadowColors, _cornerRadius, translateY);

        // Draw background
        using var bgBrush = new SolidBrush(ThemeManager.Current.Surface1);
        using var bgPath = DrawHelpers.GetRoundedRect(rect, _cornerRadius);
        g.FillPath(bgBrush, bgPath);

        // Draw outer border
        using var borderPen = new Pen(borderColor, 1);
        g.DrawPath(borderPen, bgPath);

        // Draw inner bezel (inset 5px)
        var insetRect = new Rectangle(
            rect.X + _insetOffset,
            rect.Y + _insetOffset,
            rect.Width - _insetOffset * 2,
            rect.Height - _insetOffset * 2
        );
        var insetRadius = Math.Max(1, _cornerRadius - _insetOffset);
        using var insetPath = DrawHelpers.GetRoundedRect(insetRect, insetRadius);
        using var insetPen = new Pen(Color.FromArgb((int)(0.045 * 255 * _hoverProgress + 0.045 * 255 * (1 - _hoverProgress)), 255, 255, 255), 1);
        // Inner bezel is always subtle white 0.045 opacity
        using var insetPenFixed = new Pen(Color.FromArgb(11, 255, 255, 255), 1); // 0.045 * 255 ≈ 11
        g.DrawPath(insetPenFixed, insetPath);

        // Inner highlight on hover
        if (_hoverProgress > 0)
        {
            using var highlightPen = new Pen(Color.FromArgb((int)(0.05 * 255 * _hoverProgress), 255, 255, 255), 1);
            g.DrawPath(highlightPen, insetPath);
        }
    }

    public void RefreshTheme()
    {
        Invalidate();
    }
}