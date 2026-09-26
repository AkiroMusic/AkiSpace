using System.Windows.Media;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Color = System.Windows.Media.Color;

namespace AkiSpace.Common;

/// <summary>
/// Aurora Glass design tokens ("Aki-Design-System.md").
/// A theme is a self-contained color pack: base trio, text trio, accent trio,
/// the three-color gradient ramp (the single global light source), the four
/// aurora glow intensities, and the glass/material tokens. Every effect layer
/// (aurora backdrop, liquid cards, shadows) reads semantic tokens only, so a
/// new pack is just another entry in <see cref="Palettes"/>.
/// </summary>
public static class ThemeTokens
{
    public static readonly Dictionary<string, ThemePalette> Palettes = new()
    {
        ["dark"] = DarkPack(),
        ["amber"] = AmberPack(),
        ["mint"] = MintPack(),
        ["pearl"] = PearlPack(),
    };

    private static ThemePalette DarkPack() => new()
    {
        Name = "dark",
        BgBase = Rgb(0x0C, 0x12, 0x20),
        Surface1 = Rgb(0x15, 0x1C, 0x2C),
        Surface2 = Rgb(0x1E, 0x28, 0x39),
        Border = Rgb(0x2B, 0x38, 0x52),
        TextPrimary = Rgb(0xEE, 0xF2, 0xF8),
        TextSecondary = Rgb(0x8C, 0x97, 0xAC),
        TextTertiary = Rgb(0x5A, 0x64, 0x78),
        Accent = Rgb(0x6D, 0x82, 0xFF),
        AccentHover = Rgb(0x8A, 0x9B, 0xFF),
        AccentSecondary = Rgb(0x2C, 0xC5, 0xE0),
        AccentTertiary = Rgb(0xF0, 0xA0, 0xD8),
        Success = Rgb(0x4F, 0xAE, 0x8A),
        Error = Rgb(0xD9, 0x69, 0x5F),
        Warning = Rgb(0xE8, 0xA3, 0x3D),
        GradA = Rgb(0x48, 0x60, 0xD9),
        GradB = Rgb(0x2C, 0xC5, 0xE0),
        GradC = Rgb(0xC4, 0xA8, 0xF5),
        Aurora1 = 0x4D, // 30%
        Aurora2 = 0x38, // 22%
        Aurora3 = 0x24, // 14%
        Aurora4 = 0x1A, // 10%
        GlassBg = Argb(0x9E, 0x15, 0x1C, 0x2C),      // rgba(21, 28, 44, 0.62)
        GlassBgStrong = Argb(0xD1, 0x15, 0x1C, 0x2C), // rgba(21, 28, 44, 0.82)
        GlassBorder = Argb(0x14, 0xFF, 0xFF, 0xFF),   // rgba(255, 255, 255, 0.08)
        GlassBlur = 16,
        LiquidBg = Argb(0x99, 0x15, 0x1C, 0x2C),      // rgba(21, 28, 44, 0.60)
        LiquidBorder = Argb(0x17, 0xFF, 0xFF, 0xFF),  // rgba(255, 255, 255, 0.09)
        Specular = Argb(0x1A, 0xFF, 0xFF, 0xFF),      // rgba(255, 255, 255, 0.10)
        InnerShade = Argb(0x38, 0x00, 0x00, 0x00),    // rgba(0, 0, 0, 0.22)
        BezelInnerLine = Argb(0x0B, 0xFF, 0xFF, 0xFF), // rgba(255, 255, 255, 0.045)
        ShadowTint = Rgb(0x04, 0x06, 0x0C),
    };

    private static ThemePalette AmberPack() => new()
    {
        Name = "amber",
        BgBase = Rgb(0x1A, 0x12, 0x10),
        Surface1 = Rgb(0x26, 0x19, 0x16),
        Surface2 = Rgb(0x32, 0x21, 0x1C),
        Border = Rgb(0x4A, 0x33, 0x2B),
        TextPrimary = Rgb(0xF5, 0xED, 0xE4),
        TextSecondary = Rgb(0xA8, 0x96, 0x88),
        TextTertiary = Rgb(0x6E, 0x5F, 0x52),
        Accent = Rgb(0xE5, 0x8A, 0x5A),
        AccentHover = Rgb(0xF0, 0xA2, 0x76),
        AccentSecondary = Rgb(0xF2, 0xC2, 0x8F),
        AccentTertiary = Rgb(0xD9, 0x8E, 0x9C),
        Success = Rgb(0x58, 0xA8, 0x73),
        Error = Rgb(0xE0, 0x65, 0x52),
        Warning = Rgb(0xE8, 0xA3, 0x3D),
        GradA = Rgb(0xB8, 0x4A, 0x3A),
        GradB = Rgb(0xE5, 0x8A, 0x5A),
        GradC = Rgb(0xF2, 0xC2, 0x8F),
        Aurora1 = 0x42, // 26%
        Aurora2 = 0x30, // 19%
        Aurora3 = 0x1F, // 12%
        Aurora4 = 0x17, // 9%
        GlassBg = Argb(0x9E, 0x26, 0x19, 0x16),
        GlassBgStrong = Argb(0xD1, 0x26, 0x19, 0x16),
        GlassBorder = Argb(0x14, 0xFF, 0xFF, 0xFF),
        GlassBlur = 16,
        LiquidBg = Argb(0x99, 0x26, 0x19, 0x16),
        LiquidBorder = Argb(0x17, 0xFF, 0xFF, 0xFF),
        Specular = Argb(0x1A, 0xFF, 0xFF, 0xFF),
        InnerShade = Argb(0x38, 0x00, 0x00, 0x00),
        BezelInnerLine = Argb(0x0B, 0xFF, 0xFF, 0xFF),
        ShadowTint = Rgb(0x10, 0x08, 0x05),
    };

    private static ThemePalette MintPack() => new()
    {
        Name = "mint",
        BgBase = Rgb(0xE8, 0xF0, 0xE5),
        Surface1 = Rgb(0xF4, 0xF8, 0xF0),
        Surface2 = Rgb(0xDC, 0xE7, 0xDA),
        Border = Rgb(0xC9, 0xD8, 0xC6),
        TextPrimary = Rgb(0x2F, 0x4A, 0x3A),
        TextSecondary = Rgb(0x6B, 0x72, 0x68),
        TextTertiary = Rgb(0x98, 0xA6, 0x9A),
        Accent = Rgb(0x3D, 0x62, 0x4C),
        AccentHover = Rgb(0x32, 0x51, 0x3F),
        AccentSecondary = Rgb(0x8F, 0xA8, 0x9A),
        AccentTertiary = Rgb(0xD9, 0xA3, 0x8E),
        Success = Rgb(0x4E, 0x8F, 0x68),
        Error = Rgb(0xC4, 0x58, 0x4E),
        Warning = Rgb(0xC0, 0x8A, 0x3D),
        GradA = Rgb(0x8F, 0xA8, 0x9A),
        GradB = Rgb(0xA3, 0xC4, 0xA9),
        GradC = Rgb(0xB5, 0xD7, 0xC3),
        Aurora1 = 0x26, // 15%
        Aurora2 = 0x1A, // 10%
        Aurora3 = 0x0F, // 6%
        Aurora4 = 0x0A, // 4%
        GlassBg = Argb(0xAD, 0xF4, 0xF8, 0xF0),      // rgba(244, 248, 240, 0.68)
        GlassBgStrong = Argb(0xE0, 0xF4, 0xF8, 0xF0), // rgba(244, 248, 240, 0.88)
        GlassBorder = Argb(0x14, 0x2F, 0x4A, 0x3A),   // rgba(47, 74, 58, 0.08)
        GlassBlur = 16,
        LiquidBg = Argb(0x8C, 0xFF, 0xFF, 0xFF),      // rgba(255, 255, 255, 0.55)
        LiquidBorder = Argb(0xA6, 0xFF, 0xFF, 0xFF),  // rgba(255, 255, 255, 0.65)
        Specular = Argb(0xD9, 0xFF, 0xFF, 0xFF),      // rgba(255, 255, 255, 0.85)
        InnerShade = Argb(0x14, 0x2F, 0x3A, 0x32),    // rgba(47, 58, 50, 0.08)
        BezelInnerLine = Argb(0x0A, 0x00, 0x00, 0x00), // rgba(0, 0, 0, 0.04)
        ShadowTint = Rgb(0x2F, 0x3A, 0x32),
        LightTheme = true,
    };

    private static ThemePalette PearlPack() => new()
    {
        Name = "pearl",
        BgBase = Rgb(0xF0, 0xE8, 0xEE),
        Surface1 = Rgb(0xFA, 0xF5, 0xF8),
        Surface2 = Rgb(0xE4, 0xD8, 0xE2),
        Border = Rgb(0xD4, 0xC4, 0xD2),
        TextPrimary = Rgb(0x3A, 0x2F, 0x3C),
        TextSecondary = Rgb(0x8A, 0x7A, 0x8C),
        TextTertiary = Rgb(0xB4, 0xA6, 0xB6),
        Accent = Rgb(0xB2, 0x7A, 0x94),
        AccentHover = Rgb(0x9C, 0x65, 0x80),
        AccentSecondary = Rgb(0x9B, 0x86, 0xBE),
        AccentTertiary = Rgb(0x7E, 0x9C, 0xC0),
        Success = Rgb(0x5A, 0x9E, 0x7A),
        Error = Rgb(0xC4, 0x58, 0x68),
        Warning = Rgb(0xC0, 0x8A, 0x3D),
        GradA = Rgb(0xD9, 0xAF, 0xC0),
        GradB = Rgb(0xC8, 0xB7, 0xD8),
        GradC = Rgb(0xAF, 0xC5, 0xDE),
        Aurora1 = 0x29, // 16%
        Aurora2 = 0x1C, // 11%
        Aurora3 = 0x12, // 7%
        Aurora4 = 0x0D, // 5%
        GlassBg = Argb(0xAD, 0xFA, 0xF5, 0xF8),
        GlassBgStrong = Argb(0xE0, 0xFA, 0xF5, 0xF8),
        GlassBorder = Argb(0x14, 0x3A, 0x2A, 0x34),   // rgba(58, 42, 52, 0.08)
        GlassBlur = 16,
        LiquidBg = Argb(0x8C, 0xFF, 0xFF, 0xFF),
        LiquidBorder = Argb(0xA6, 0xFF, 0xFF, 0xFF),
        Specular = Argb(0xD9, 0xFF, 0xFF, 0xFF),
        InnerShade = Argb(0x14, 0x3A, 0x2A, 0x34),    // rgba(58, 42, 52, 0.08)
        BezelInnerLine = Argb(0x0A, 0x00, 0x00, 0x00),
        ShadowTint = Rgb(0x3A, 0x2A, 0x34),
        LightTheme = true,
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
}

/// <summary>One Aurora Glass color pack. Names are the semantic tokens; values are the pack.</summary>
public sealed class ThemePalette
{
    public string Name { get; set; } = "dark";
    public bool LightTheme { get; set; }

    // Base trio + border
    public Color BgBase { get; set; }
    public Color Surface1 { get; set; }
    public Color Surface2 { get; set; }
    public Color Border { get; set; }

    // Text trio
    public Color TextPrimary { get; set; }
    public Color TextSecondary { get; set; }
    public Color TextTertiary { get; set; }

    // Accent trio + status
    public Color Accent { get; set; }
    public Color AccentHover { get; set; }
    public Color AccentSecondary { get; set; }
    public Color AccentTertiary { get; set; }
    public Color Success { get; set; }
    public Color Error { get; set; }
    public Color Warning { get; set; }

    // Gradient ramp — the global light source: A (deep/structure) → B (mid/atmosphere) → C (light/highlight)
    public Color GradA { get; set; }
    public Color GradB { get; set; }
    public Color GradC { get; set; }

    // Aurora glow intensities for the four background glows (light packs run lower)
    public byte Aurora1 { get; set; }
    public byte Aurora2 { get; set; }
    public byte Aurora3 { get; set; }
    public byte Aurora4 { get; set; }

    // Frosted (structural glass) tokens
    public Color GlassBg { get; set; }
    public Color GlassBgStrong { get; set; }
    public Color GlassBorder { get; set; }
    public int GlassBlur { get; set; } = 16;

    // Liquid (content glass, Lite — no backdrop) tokens; specular and inner shade always travel in pairs
    public Color LiquidBg { get; set; }
    public Color LiquidBorder { get; set; }
    public Color Specular { get; set; }
    public Color InnerShade { get; set; }
    public Color BezelInnerLine { get; set; }

    // Tinted shadow color (never pure black)
    public Color ShadowTint { get; set; }
}
