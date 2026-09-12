using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Common;

/// <summary>
/// Central theme manager - handles 6-theme switching, persistence, and token access.
/// All UI code must use ThemeManager.Current.GetColor(), GetFont(), etc.
/// Zero hardcoded colors/fonts anywhere.
/// </summary>
public sealed class ThemeManager
{
    private static readonly Lazy<ThemeManager> _instance = new(() => new ThemeManager());
    public static ThemeManager Current => _instance.Value;

    private readonly object _gate = new();
    private ThemePalette _currentPalette;
    private ILogger? _logger;
    private SettingsService? _settingsService;

    // Public readonly properties for direct access (updated on theme change)
    public Color BgBase { get; private set; }
    public Color Surface1 { get; private set; }
    public Color Surface2 { get; private set; }
    public Color Border { get; private set; }
    public Color TextPrimary { get; private set; }
    public Color TextSecondary { get; private set; }
    public Color TextTertiary { get; private set; }
    public Color Accent { get; private set; }
    public Color AccentHover { get; private set; }
    public Color AccentSecondary { get; private set; }
    public Color AccentTertiary { get; private set; }
    public Color Success { get; private set; }
    public Color Error { get; private set; }
    public Color Warning { get; private set; }
    public Color[] Shadow1 { get; private set; } = Array.Empty<Color>();
    public Color[] Shadow2 { get; private set; } = Array.Empty<Color>();
    public Color[] Shadow3 { get; private set; } = Array.Empty<Color>();
    public Color[] ShadowAccent { get; private set; } = Array.Empty<Color>();
    public Color GlassBg { get; private set; }
    public Color GlassBgStrong { get; private set; }
    public Color GlassBorder { get; private set; }
    public int GlassBlur { get; private set; } = 20;
    public (RectangleF Rect, Color Color)[] AmbientGlows { get; private set; } = Array.Empty<(RectangleF, Color)>();

    public string CurrentThemeName { get; private set; } = "dark";

    private ThemeManager()
    {
        _currentPalette = ThemeTokens.Palettes["dark"];
        ApplyPalette(_currentPalette);
    }

    /// <summary>
    /// Initialize with logger and settings service for persistence.
    /// Call from Program.cs after DI container is built.
    /// </summary>
    public void Initialize(ILogger<ThemeManager> logger, SettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;

        // Load persisted theme
        var theme = _settingsService.Current.Theme ?? "dark";
        SetTheme(theme, persist: false);
    }

    /// <summary>
    /// Set the active theme by name. Valid names are the keys of <see cref="ThemeTokens.Palettes"/>.
    /// </summary>
    public void SetTheme(string themeName, bool persist = true)
    {
        lock (_gate)
        {
            if (!ThemeTokens.Palettes.TryGetValue(themeName, out var palette))
            {
                _logger?.LogWarning("Unknown theme '{Theme}', falling back to dark", themeName);
                palette = ThemeTokens.Palettes["dark"];
                themeName = "dark";
            }

            _currentPalette = palette;
            CurrentThemeName = themeName;
            ApplyPalette(palette);

            if (persist && _settingsService != null)
            {
                _settingsService.Update(s => s.Theme = themeName);
            }

            _logger?.LogInformation("Theme changed to {Theme}", themeName);
            ThemeChanged?.Invoke(this, themeName);
        }
    }

    /// <summary>
    /// Event fired when theme changes - UI should refresh.
    /// </summary>
    public event EventHandler<string>? ThemeChanged;

    /// <summary>
    /// Get color by semantic token name.
    /// </summary>
    public Color GetColor(string token)
    {
        return token switch
        {
            ColorToken.BgBase => BgBase,
            ColorToken.Surface1 => Surface1,
            ColorToken.Surface2 => Surface2,
            ColorToken.Border => Border,
            ColorToken.TextPrimary => TextPrimary,
            ColorToken.TextSecondary => TextSecondary,
            ColorToken.TextTertiary => TextTertiary,
            ColorToken.Accent => Accent,
            ColorToken.AccentHover => AccentHover,
            ColorToken.AccentSecondary => AccentSecondary,
            ColorToken.AccentTertiary => AccentTertiary,
            ColorToken.Success => Success,
            ColorToken.Error => Error,
            ColorToken.Warning => Warning,
            ColorToken.GlassBg => GlassBg,
            ColorToken.GlassBgStrong => GlassBgStrong,
            ColorToken.GlassBorder => GlassBorder,
            _ => Color.Magenta, // Visible error for invalid tokens
        };
    }

    /// <summary>
    /// Get shadow colors for elevation level (1-3 or "accent").
    /// </summary>
    public Color[] GetShadow(string level)
    {
        return level switch
        {
            "1" => Shadow1,
            "2" => Shadow2,
            "3" => Shadow3,
            "accent" => ShadowAccent,
            _ => Shadow1,
        };
    }

    /// <summary>
    /// UI Sans font (Plus Jakarta Sans) with size/style.
    /// </summary>
    public Font GetFontSans(float size, FontStyle style = FontStyle.Regular) =>
        MakeFont(ThemeTokens.Typography.Sans, size, style, FontFamily.GenericSansSerif);

    /// <summary>
    /// Display font (Fraunces) with size/style.
    /// </summary>
    public Font GetFontDisplay(float size, FontStyle style = FontStyle.Regular) =>
        MakeFont(ThemeTokens.Typography.Display, size, style, FontFamily.GenericSansSerif);

    /// <summary>
    /// Mono font (IBM Plex Mono) with size/style.
    /// </summary>
    public Font GetFontMono(float size, FontStyle style = FontStyle.Regular) =>
        MakeFont(ThemeTokens.Typography.Mono, size, style, FontFamily.GenericMonospace);

    /// <summary>
    /// Resolve a system font family by name, falling back to <paramref name="fallbackFamily"/>
    /// when the named family is not installed. Sizes are in pixels (GraphicsUnit.Pixel),
    /// preserving the unit semantics every call site was built against.
    /// </summary>
    private static Font MakeFont(string familyName, float size, FontStyle style, FontFamily fallbackFamily)
    {
        try
        {
            using var ff = new FontFamily(familyName);
            return new Font(ff, size, style, GraphicsUnit.Pixel);
        }
        catch
        {
            return new Font(fallbackFamily, size, style, GraphicsUnit.Pixel);
        }
    }

    /// <summary>
    /// Spacing tokens.
    /// </summary>
    public int Space(int level) => level switch
    {
        1 => ThemeTokens.Space.S1,
        2 => ThemeTokens.Space.S2,
        3 => ThemeTokens.Space.S3,
        4 => ThemeTokens.Space.S4,
        6 => ThemeTokens.Space.S6,
        8 => ThemeTokens.Space.S8,
        12 => ThemeTokens.Space.S12,
        _ => ThemeTokens.Space.S4,
    };

    /// <summary>
    /// Border radius tokens.
    /// </summary>
    public int Radius(string size) => size switch
    {
        "sm" => ThemeTokens.Radius.Sm,
        "md" => ThemeTokens.Radius.Md,
        "lg" => ThemeTokens.Radius.Lg,
        "full" => ThemeTokens.Radius.Full,
        _ => ThemeTokens.Radius.Sm,
    };

    /// <summary>
    /// Motion/easing tokens.
    /// </summary>
    public (float[] easing, int duration) GetMotion(string type) => type switch
    {
        "hover" => (ThemeTokens.Motion.EaseDefault, ThemeTokens.Motion.DurationHover),
        "panel" => (ThemeTokens.Motion.EaseDefault, ThemeTokens.Motion.DurationPanel),
        "spring" => (ThemeTokens.Motion.EaseSpring, ThemeTokens.Motion.DurationSpring),
        "out-quint" => (ThemeTokens.Motion.EaseOutQuint, ThemeTokens.Motion.DurationPanel),
        _ => (ThemeTokens.Motion.EaseDefault, ThemeTokens.Motion.DurationHover),
    };

    private void ApplyPalette(ThemePalette p)
    {
        BgBase = p.GetColor(p.BgBase);
        Surface1 = p.GetColor(p.Surface1);
        Surface2 = p.GetColor(p.Surface2);
        Border = p.GetColor(p.Border);
        TextPrimary = p.GetColor(p.TextPrimary);
        TextSecondary = p.GetColor(p.TextSecondary);
        TextTertiary = p.GetColor(p.TextTertiary);
        Accent = p.GetColor(p.Accent);
        AccentHover = p.GetColor(p.AccentHover);
        AccentSecondary = p.GetColor(p.AccentSecondary);
        AccentTertiary = p.GetColor(p.AccentTertiary);
        Success = p.GetColor(p.Success);
        Error = p.GetColor(p.Error);
        Warning = p.GetColor(p.Warning);
        Shadow1 = p.Shadow1;
        Shadow2 = p.Shadow2;
        Shadow3 = p.Shadow3;
        ShadowAccent = p.ShadowAccent;
        GlassBg = p.GlassBg;
        GlassBgStrong = p.GlassBgStrong;
        GlassBorder = p.GlassBorder;
        GlassBlur = p.GlassBlur;
        AmbientGlows = p.AmbientGlows;
    }
}