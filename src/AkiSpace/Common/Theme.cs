using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AkiSpace.Common;

/// <summary>
/// Ethereal Glass Design System - Complete Token Definitions
/// All colors, spacing, radii, shadows, motion, glass, typography tokens.
/// Zero hardcoded values allowed in UI code - everything via ThemeManager.Current.
/// </summary>
public static class ThemeTokens
{
    // ============================================================
    // THEME DEFINITIONS (matching Aki-Design-System.md exactly)
    // ============================================================

    public static readonly Dictionary<string, ThemePalette> Palettes = new()
    {
        ["dark"] = new ThemePalette
        {
            Name = "dark",
            // Core surfaces
            BgBase = 0x0E1016u,
            Surface1 = 0x171A23u,
            Surface2 = 0x212531u,
            Border = 0x2E3342u,
            // Text
            TextPrimary = 0xF0EEE7u,
            TextSecondary = 0x8D93A3u,
            TextTertiary = 0x5B606Eu,
            // Accents
            Accent = 0x6C8CFFu,
            AccentHover = 0x86A3FFu,
            AccentSecondary = 0xA78BFAu,
            AccentTertiary = 0xF0A0D8u,
            // Status
            Success = 0x4FAE8Au,
            Error = 0xD9695Fu,
            Warning = 0xE8A33Du,
            // Shadows (Dark tint: rgba(4,6,12,...))
            Shadow1 = new[] { Color.FromArgb(102, 4, 6, 12), Color.FromArgb(46, 4, 6, 12) },
            Shadow2 = new[] { Color.FromArgb(102, 4, 6, 12), Color.FromArgb(71, 4, 6, 12) },
            Shadow3 = new[] { Color.FromArgb(115, 4, 6, 12), Color.FromArgb(97, 4, 6, 12) },
            ShadowAccent = new[] { Color.FromArgb(64, 108, 140, 255), Color.FromArgb(46, 108, 140, 255) },
            // Glass
            GlassBg = Color.FromArgb(158, 23, 26, 35),      // 0.62 * 255
            GlassBgStrong = Color.FromArgb(210, 23, 26, 35), // 0.82 * 255
            GlassBorder = Color.FromArgb(20, 255, 255, 255), // 0.08 * 255
            GlassBlur = 20,
            // Ambient gradients (4 radial glows)
            AmbientGlows = new[]
            {
                (Rect: new RectangleF(0.12f, 0.08f, 0.38f, 0.44f), Color: Color.FromArgb(36, 108, 140, 255)),  // 0.14 * 255
                (Rect: new RectangleF(0.92f, 0.12f, 0.42f, 0.48f), Color: Color.FromArgb(28, 167, 139, 250)),  // 0.11 * 255
                (Rect: new RectangleF(0.82f, 0.92f, 0.50f, 0.42f), Color: Color.FromArgb(20, 108, 140, 255)),  // 0.08 * 255
                (Rect: new RectangleF(0.06f, 0.88f, 0.36f, 0.40f), Color: Color.FromArgb(18, 240, 160, 216)),  // 0.07 * 255
            }
        },

        ["light"] = new ThemePalette
        {
            Name = "light",
            BgBase = 0xF5F5F7u,
            Surface1 = 0xFFFFFFu,
            Surface2 = 0xEBEBEDu,
            Border = 0xD1D1D6u,
            TextPrimary = 0x1C1C1Eu,
            TextSecondary = 0x6C6C70u,
            TextTertiary = 0xAEAEB2u,
            Accent = 0x5B7FFFu,
            AccentHover = 0x4A6FE8u,
            AccentSecondary = 0x8B6FE8u,
            AccentTertiary = 0xD4893Bu,
            Success = 0x34C759u,
            Error = 0xFF3B30u,
            Warning = 0xE8A33Du,
            Shadow1 = new[] { Color.FromArgb(15, 20, 20, 30), Color.FromArgb(13, 20, 20, 30) },
            Shadow2 = new[] { Color.FromArgb(18, 20, 20, 30), Color.FromArgb(23, 20, 20, 30) },
            Shadow3 = new[] { Color.FromArgb(20, 20, 20, 30), Color.FromArgb(31, 20, 20, 30) },
            ShadowAccent = new[] { Color.FromArgb(51, 91, 127, 255), Color.FromArgb(41, 91, 127, 255) },
            GlassBg = Color.FromArgb(174, 255, 255, 255),     // 0.68 * 255
            GlassBgStrong = Color.FromArgb(225, 255, 255, 255), // 0.88 * 255
            GlassBorder = Color.FromArgb(20, 28, 28, 30),      // 0.08 * 255
            GlassBlur = 20,
            AmbientGlows = new[]
            {
                (Rect: new RectangleF(0.12f, 0.08f, 0.38f, 0.44f), Color: Color.FromArgb(23, 91, 127, 255)),
                (Rect: new RectangleF(0.92f, 0.12f, 0.42f, 0.48f), Color: Color.FromArgb(18, 139, 111, 232)),
                (Rect: new RectangleF(0.82f, 0.92f, 0.50f, 0.42f), Color: Color.FromArgb(13, 91, 127, 255)),
                (Rect: new RectangleF(0.06f, 0.88f, 0.36f, 0.40f), Color: Color.FromArgb(13, 212, 137, 59)),
            }
        },

        ["sepia"] = new ThemePalette
        {
            Name = "sepia",
            BgBase = 0xEDE0CCu,
            Surface1 = 0xF5ECD8u,
            Surface2 = 0xE3D4BCu,
            Border = 0xCAB892u,
            TextPrimary = 0x3A2C1Au,
            TextSecondary = 0x7A6A4Au,
            TextTertiary = 0xA89878u,
            Accent = 0xC9A24Bu,
            AccentHover = 0xB89130u,
            AccentSecondary = 0xD4893Bu,
            AccentTertiary = 0xC45A4Au,
            Success = 0x5A8A6Au,
            Error = 0xC45A4Au,
            Warning = 0xC98A2Au,
            Shadow1 = new[] { Color.FromArgb(26, 58, 44, 26), Color.FromArgb(20, 58, 44, 26) },
            Shadow2 = new[] { Color.FromArgb(26, 58, 44, 26), Color.FromArgb(31, 58, 44, 26) },
            Shadow3 = new[] { Color.FromArgb(31, 58, 44, 26), Color.FromArgb(41, 58, 44, 26) },
            ShadowAccent = new[] { Color.FromArgb(51, 201, 162, 75), Color.FromArgb(41, 201, 162, 75) },
            GlassBg = Color.FromArgb(179, 245, 236, 216),    // 0.70 * 255
            GlassBgStrong = Color.FromArgb(230, 245, 236, 216), // 0.90 * 255
            GlassBorder = Color.FromArgb(26, 58, 44, 26),     // 0.10 * 255
            GlassBlur = 20,
            AmbientGlows = new[]
            {
                (Rect: new RectangleF(0.12f, 0.08f, 0.38f, 0.44f), Color: Color.FromArgb(26, 201, 162, 75)),
                (Rect: new RectangleF(0.92f, 0.12f, 0.42f, 0.48f), Color: Color.FromArgb(20, 212, 137, 59)),
                (Rect: new RectangleF(0.82f, 0.92f, 0.50f, 0.42f), Color: Color.FromArgb(15, 196, 90, 74)),
                (Rect: new RectangleF(0.06f, 0.88f, 0.36f, 0.40f), Color: Color.FromArgb(15, 196, 90, 74)),
            }
        },

        ["forest"] = new ThemePalette
        {
            Name = "forest",
            BgBase = 0x283A24u,
            Surface1 = 0x324830u,
            Surface2 = 0x3C5438u,
            Border = 0x4E6446u,
            TextPrimary = 0xE0EDD8u,
            TextSecondary = 0x8A9E80u,
            TextTertiary = 0x5A7050u,
            Accent = 0x6CC96Cu,
            AccentHover = 0x7ED87Eu,
            AccentSecondary = 0x5ABB9Au,
            AccentTertiary = 0xD9695Fu,
            Success = 0x4FAE6Au,
            Error = 0xD9695Fu,
            Warning = 0xD9A33Du,
            Shadow1 = new[] { Color.FromArgb(89, 8, 16, 8), Color.FromArgb(51, 8, 16, 8) },
            Shadow2 = new[] { Color.FromArgb(89, 8, 16, 8), Color.FromArgb(64, 8, 16, 8) },
            Shadow3 = new[] { Color.FromArgb(102, 8, 16, 8), Color.FromArgb(77, 8, 16, 8) },
            ShadowAccent = new[] { Color.FromArgb(51, 108, 201, 108), Color.FromArgb(41, 108, 201, 108) },
            GlassBg = Color.FromArgb(166, 50, 72, 48),     // 0.65 * 255
            GlassBgStrong = Color.FromArgb(217, 50, 72, 48), // 0.85 * 255
            GlassBorder = Color.FromArgb(20, 255, 255, 255), // 0.08 * 255
            GlassBlur = 20,
            AmbientGlows = new[]
            {
                (Rect: new RectangleF(0.12f, 0.08f, 0.38f, 0.44f), Color: Color.FromArgb(26, 108, 201, 108)),
                (Rect: new RectangleF(0.92f, 0.12f, 0.42f, 0.48f), Color: Color.FromArgb(20, 90, 187, 154)),
                (Rect: new RectangleF(0.82f, 0.92f, 0.50f, 0.42f), Color: Color.FromArgb(15, 108, 201, 108)),
                (Rect: new RectangleF(0.06f, 0.88f, 0.36f, 0.40f), Color: Color.FromArgb(15, 217, 99, 95)),
            }
        },

        ["ocean"] = new ThemePalette
        {
            Name = "ocean",
            BgBase = 0x162544u,
            Surface1 = 0x1E3050u,
            Surface2 = 0x283C60u,
            Border = 0x3A4E70u,
            TextPrimary = 0xE2E8F0u,
            TextSecondary = 0x8DA0C0u,
            TextTertiary = 0x5A6E90u,
            Accent = 0x50C8F0u,
            AccentHover = 0x6DD4FFu,
            AccentSecondary = 0x8A96FFu,
            AccentTertiary = 0xE86A5Fu,
            Success = 0x4AAF8Au,
            Error = 0xE86A5Fu,
            Warning = 0xE8B03Du,
            Shadow1 = new[] { Color.FromArgb(102, 4, 10, 24), Color.FromArgb(56, 4, 10, 24) },
            Shadow2 = new[] { Color.FromArgb(102, 4, 10, 24), Color.FromArgb(77, 4, 10, 24) },
            Shadow3 = new[] { Color.FromArgb(115, 4, 10, 24), Color.FromArgb(92, 4, 10, 24) },
            ShadowAccent = new[] { Color.FromArgb(51, 80, 200, 240), Color.FromArgb(41, 80, 200, 240) },
            GlassBg = Color.FromArgb(169, 30, 48, 80),    // 0.66 * 255
            GlassBgStrong = Color.FromArgb(220, 30, 48, 80), // 0.86 * 255
            GlassBorder = Color.FromArgb(20, 255, 255, 255), // 0.08 * 255
            GlassBlur = 20,
            AmbientGlows = new[]
            {
                (Rect: new RectangleF(0.12f, 0.08f, 0.38f, 0.44f), Color: Color.FromArgb(26, 80, 200, 240)),
                (Rect: new RectangleF(0.92f, 0.12f, 0.42f, 0.48f), Color: Color.FromArgb(20, 138, 150, 255)),
                (Rect: new RectangleF(0.82f, 0.92f, 0.50f, 0.42f), Color: Color.FromArgb(15, 80, 200, 240)),
                (Rect: new RectangleF(0.06f, 0.88f, 0.36f, 0.40f), Color: Color.FromArgb(15, 232, 106, 95)),
            }
        },

        ["lavender"] = new ThemePalette
        {
            Name = "lavender",
            BgBase = 0x241E38u,
            Surface1 = 0x2E2745u,
            Surface2 = 0x383052u,
            Border = 0x4A3E6Eu,
            TextPrimary = 0xECE6F5u,
            TextSecondary = 0xB0A0D0u,
            TextTertiary = 0x7A6A9Au,
            Accent = 0xB89DFFu,
            AccentHover = 0xCAB0FFu,
            AccentSecondary = 0xF0A0D8u,
            AccentTertiary = 0xE86A7Au,
            Success = 0x6AAF8Au,
            Error = 0xE86A7Au,
            Warning = 0xD9A33Du,
            Shadow1 = new[] { Color.FromArgb(102, 12, 8, 24), Color.FromArgb(56, 12, 8, 24) },
            Shadow2 = new[] { Color.FromArgb(102, 12, 8, 24), Color.FromArgb(77, 12, 8, 24) },
            Shadow3 = new[] { Color.FromArgb(115, 12, 8, 24), Color.FromArgb(92, 12, 8, 24) },
            ShadowAccent = new[] { Color.FromArgb(51, 184, 157, 255), Color.FromArgb(41, 184, 157, 255) },
            GlassBg = Color.FromArgb(169, 46, 39, 69),    // 0.66 * 255
            GlassBgStrong = Color.FromArgb(220, 46, 39, 69), // 0.86 * 255
            GlassBorder = Color.FromArgb(20, 255, 255, 255), // 0.08 * 255
            GlassBlur = 20,
            AmbientGlows = new[]
            {
                (Rect: new RectangleF(0.12f, 0.08f, 0.38f, 0.44f), Color: Color.FromArgb(26, 184, 157, 255)),
                (Rect: new RectangleF(0.92f, 0.12f, 0.42f, 0.48f), Color: Color.FromArgb(20, 240, 160, 216)),
                (Rect: new RectangleF(0.82f, 0.92f, 0.50f, 0.42f), Color: Color.FromArgb(15, 184, 157, 255)),
                (Rect: new RectangleF(0.06f, 0.88f, 0.36f, 0.40f), Color: Color.FromArgb(15, 232, 106, 122)),
            }
        }
    };

    // ============================================================
    // SHARED TOKENS (same across all themes)
    // ============================================================

    public static class Radius
    {
        public const int Sm = 10;
        public const int Md = 16;
        public const int Lg = 24;
        public const int Full = 999;
    }

    public static class Space
    {
        public const int S1 = 4;
        public const int S2 = 8;
        public const int S3 = 12;
        public const int S4 = 16;
        public const int S6 = 24;
        public const int S8 = 32;
        public const int S12 = 48;
    }

    public static class Motion
    {
        // cubic-bezier(0.2, 0.8, 0.2, 1)
        public static readonly float[] EaseDefault = { 0.2f, 0.8f, 0.2f, 1f };
        // cubic-bezier(0.32, 0.72, 0, 1) - SPRING
        public static readonly float[] EaseSpring = { 0.32f, 0.72f, 0f, 1f };
        // cubic-bezier(0.22, 1, 0.36, 1)
        public static readonly float[] EaseOutQuint = { 0.22f, 1f, 0.36f, 1f };

        public const int DurationHover = 150;
        public const int DurationPanel = 220;
        public const int DurationSpring = 320;
    }

    public static class Typography
    {
        // Font family names (registered via FontLoader)
        public const string Sans = "Plus Jakarta Sans";
        public const string Display = "Fraunces";
        public const string Mono = "IBM Plex Mono";

        // Fallback stacks
        public static readonly string[] SansFallback = { "Microsoft YaHei UI", "Segoe UI", "system-ui", "sans-serif" };
        public static readonly string[] DisplayFallback = { "Georgia", "serif" };
        public static readonly string[] MonoFallback = { "Consolas", "Courier New", "monospace" };

        // Font sizes (px)
        public const float CardTitle = 20f;      // h2, Fraunces 600
        public const float CardSubtitle = 12f;   // tertiary, 400
        public const float MainParam = 20f;      // BPM, Mono 600
        public const float LargeValue = 14f;     // panel title, Mono 500-600
        public const float Body = 13f;           // regular UI, Sans 400-500
        public const float Label = 12f;          // secondary/tertiary
        public const float MicroLabel = 10f;     // sidebar, badge, letter-spacing 0.02em
        public const float Footer = 11f;         // tertiary
    }

    // Noise overlay opacity (0.035)
    public const float NoiseOpacity = 0.035f;
}

/// <summary>
/// Single theme palette with all color values.
/// </summary>
public sealed class ThemePalette
{
    public string Name { get; set; } = "dark";

    // Core
    public uint BgBase { get; set; }
    public uint Surface1 { get; set; }
    public uint Surface2 { get; set; }
    public uint Border { get; set; }

    // Text
    public uint TextPrimary { get; set; }
    public uint TextSecondary { get; set; }
    public uint TextTertiary { get; set; }

    // Accents
    public uint Accent { get; set; }
    public uint AccentHover { get; set; }
    public uint AccentSecondary { get; set; }
    public uint AccentTertiary { get; set; }

    // Status
    public uint Success { get; set; }
    public uint Error { get; set; }
    public uint Warning { get; set; }

    // Shadows (dual-layer: [tight, diffuse])
    public Color[] Shadow1 { get; set; } = Array.Empty<Color>();
    public Color[] Shadow2 { get; set; } = Array.Empty<Color>();
    public Color[] Shadow3 { get; set; } = Array.Empty<Color>();
    public Color[] ShadowAccent { get; set; } = Array.Empty<Color>();

    // Glass
    public Color GlassBg { get; set; }
    public Color GlassBgStrong { get; set; }
    public Color GlassBorder { get; set; }
    public int GlassBlur { get; set; } = 20;

    // Ambient background glows
    public (RectangleF Rect, Color Color)[] AmbientGlows { get; set; } = Array.Empty<(RectangleF, Color)>();

    // Helpers
    public Color GetColor(uint hex) => Color.FromArgb(unchecked((int)(0xFF000000u | hex)));
    public Color GetColor(uint hex, byte alpha) => Color.FromArgb(alpha, (int)((hex >> 16) & 0xFF), (int)((hex >> 8) & 0xFF), (int)(hex & 0xFF));
}

/// <summary>
/// Color token keys for semantic access.
/// </summary>
public static class ColorToken
{
    public const string BgBase = "bg-base";
    public const string Surface1 = "surface-1";
    public const string Surface2 = "surface-2";
    public const string Border = "border";
    public const string TextPrimary = "text-primary";
    public const string TextSecondary = "text-secondary";
    public const string TextTertiary = "text-tertiary";
    public const string Accent = "accent";
    public const string AccentHover = "accent-hover";
    public const string AccentSecondary = "accent-secondary";
    public const string AccentTertiary = "accent-tertiary";
    public const string Success = "success";
    public const string Error = "error";
    public const string Warning = "warning";
    public const string GlassBg = "glass-bg";
    public const string GlassBgStrong = "glass-bg-strong";
    public const string GlassBorder = "glass-border";
}