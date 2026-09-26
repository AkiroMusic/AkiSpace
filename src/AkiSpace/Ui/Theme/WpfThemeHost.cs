using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AkiSpace.Common;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace AkiSpace.Ui.Theme;

/// <summary>
/// Bridges the Aurora Glass color packs into WPF application resources. Every
/// surface reads semantic resource keys only — switching a pack restyles all
/// open windows without any effect-layer changes.
/// </summary>
public static class WpfThemeHost
{
    // Semantic brushes
    public const string BgBase = "Brush.BgBase";
    public const string Surface1 = "Brush.Surface1";
    public const string Surface2 = "Brush.Surface2";
    public const string Border = "Brush.Border";
    public const string TextPrimary = "Brush.TextPrimary";
    public const string TextSecondary = "Brush.TextSecondary";
    public const string TextTertiary = "Brush.TextTertiary";
    public const string Accent = "Brush.Accent";
    public const string AccentHover = "Brush.AccentHover";
    public const string AccentSecondary = "Brush.AccentSecondary";
    public const string AccentTertiary = "Brush.AccentTertiary";
    public const string Success = "Brush.Success";
    public const string Error = "Brush.Error";
    public const string Warning = "Brush.Warning";

    // Gradient ramp — the single global light source
    public const string GradA = "Brush.GradA";
    public const string GradB = "Brush.GradB";
    public const string GradC = "Brush.GradC";

    // Aurora curtain glows (ramp colors at pack light intensities)
    public const string Aurora1 = "Brush.Aurora1";
    public const string Aurora2 = "Brush.Aurora2";
    public const string Aurora3 = "Brush.Aurora3";
    public const string Aurora4 = "Brush.Aurora4";
    public const string AuroraGlow1 = "Brush.AuroraGlow1";
    public const string AuroraGlow2 = "Brush.AuroraGlow2";
    public const string AuroraGlow3 = "Brush.AuroraGlow3";
    public const string AuroraGlow4 = "Brush.AuroraGlow4";

    // Accent alpha variants (color-mix equivalents)
    public const string AccentTint = "Brush.AccentTint";        // 12% — primary button fill
    public const string AccentTintStrong = "Brush.AccentTintStrong"; // 20% — primary hover
    public const string AccentTintSoft = "Brush.AccentTintSoft";     // 10% — selected segment
    public const string AccentLine = "Brush.AccentLine";        // 20% — badge borders

    // Frosted structural glass
    public const string GlassBg = "Brush.GlassBg";
    public const string GlassBgStrong = "Brush.GlassBgStrong";
    public const string GlassBorder = "Brush.GlassBorder";

    // Liquid content glass (Lite — no backdrop); specular and inner shade travel in pairs
    public const string LiquidBg = "Brush.LiquidBg";
    public const string LiquidBorder = "Brush.LiquidBorder";
    public const string Specular = "Brush.Specular";
    public const string InnerShade = "Brush.InnerShade";
    public const string BezelLine = "Brush.BezelLine";

    // Selection signature (ramp mid-color)
    public const string Selection = "Brush.Selection";

    // Composite decoration brushes
    public const string NoiseTile = "Brush.NoiseTile";
    public const string Hairline = "Brush.Hairline";
    public const string SpecularSweep = "Brush.SpecularSweep";

    // Effects
    public const string EffectCard = "Effect.Card";
    public const string EffectAccent = "Effect.Accent";

    /// <summary>Materializes the current pack and starts tracking theme switches.</summary>
    public static void Initialize()
    {
        Application.Current.Resources["Img.NoiseTile"] = CreateNoiseTile();
        Apply();
        ThemeManager.Current.ThemeChanged += (_, _) => Apply();
    }

    public static void Apply()
    {
        var p = ThemeManager.Current.Palette;
        var dict = new ResourceDictionary
        {
            // Base + text
            [BgBase] = Brush(p.BgBase),
            [Surface1] = Brush(p.Surface1),
            [Surface2] = Brush(p.Surface2),
            [Border] = Brush(p.Border),
            [TextPrimary] = Brush(p.TextPrimary),
            [TextSecondary] = Brush(p.TextSecondary),
            [TextTertiary] = Brush(p.TextTertiary),
            // Accent + status
            [Accent] = Brush(p.Accent),
            [AccentHover] = Brush(p.AccentHover),
            [AccentSecondary] = Brush(p.AccentSecondary),
            [AccentTertiary] = Brush(p.AccentTertiary),
            [Success] = Brush(p.Success),
            [Error] = Brush(p.Error),
            [Warning] = Brush(p.Warning),
            // Ramp + aurora curtain
            [GradA] = Brush(p.GradA),
            [GradB] = Brush(p.GradB),
            [GradC] = Brush(p.GradC),
            [Aurora1] = Tinted(p.GradA, p.Aurora1),
            [Aurora2] = Tinted(p.GradB, p.Aurora2),
            [Aurora3] = Tinted(p.GradA, p.Aurora3),
            [Aurora4] = Tinted(p.GradC, p.Aurora4),
            [AuroraGlow1] = Glow(Tinted(p.GradA, p.Aurora1).Color, 0.14, 0.08, 0.42, 0.36, 0.64),
            [AuroraGlow2] = Glow(Tinted(p.GradB, p.Aurora2).Color, 0.88, 0.12, 0.38, 0.44, 0.62),
            [AuroraGlow3] = Glow(Tinted(p.GradA, p.Aurora3).Color, 0.82, 0.94, 0.48, 0.40, 0.58),
            [AuroraGlow4] = Glow(Tinted(p.GradC, p.Aurora4).Color, 0.06, 0.88, 0.30, 0.34, 0.55),
            // Accent alpha variants
            [AccentTint] = Tinted(p.Accent, 0x1F),
            [AccentTintStrong] = Tinted(p.Accent, 0x33),
            [AccentTintSoft] = Tinted(p.Accent, 0x1A),
            [AccentLine] = Tinted(p.Accent, 0x33),
            // Glass + liquid materials
            [GlassBg] = Brush(p.GlassBg),
            [GlassBgStrong] = Brush(p.GlassBgStrong),
            [GlassBorder] = Brush(p.GlassBorder),
            [LiquidBg] = Brush(p.LiquidBg),
            [LiquidBorder] = Brush(p.LiquidBorder),
            [Specular] = Brush(p.Specular),
            [InnerShade] = Brush(p.InnerShade),
            [BezelLine] = Brush(p.BezelInnerLine),
            [Selection] = Tinted(p.GradB, 0x52),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        // Styles dictionary first (loaded once), then the palette — merged lookup
        // resolves DynamicResource against every dictionary in order.
        var styles = merged.FirstOrDefault(d => (string?)d["Meta.Kind"] == "styles");
        merged.Clear();
        if (styles is not null) merged.Add(styles);
        merged.Add(dict);

        // Effects are theme-dependent; rebuild them in the app-level store.
        Application.Current.Resources[EffectCard] = new DropShadowEffect
        {
            Color = p.ShadowTint,
            BlurRadius = 24,
            ShadowDepth = 6,
            Direction = 270,
            Opacity = 0.30,
        };
        Application.Current.Resources[EffectAccent] = new DropShadowEffect
        {
            Color = p.Accent,
            BlurRadius = 20,
            ShadowDepth = 4,
            Direction = 270,
            Opacity = 0.35,
        };

        // Composite decoration brushes
        var noiseTile = CreateNoiseTile();
        var noiseBrush = new ImageBrush(noiseTile)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 160, 160),
        };
        noiseBrush.Freeze();
        Application.Current.Resources[NoiseTile] = noiseBrush;

        var hairline = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        hairline.GradientStops.Add(new GradientStop(Color.FromArgb(0, p.Border.R, p.Border.G, p.Border.B), 0));
        hairline.GradientStops.Add(new GradientStop(p.Border, 0.18));
        hairline.GradientStops.Add(new GradientStop(p.Border, 0.82));
        hairline.GradientStops.Add(new GradientStop(Color.FromArgb(0, p.Border.R, p.Border.G, p.Border.B), 1));
        hairline.Freeze();
        Application.Current.Resources[Hairline] = hairline;

        var sweep = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.6, 1),
        };
        sweep.GradientStops.Add(new GradientStop(Tinted(p.TextPrimary, 0x0D).Color, 0));
        sweep.GradientStops.Add(new GradientStop(Color.FromArgb(0, p.TextPrimary.R, p.TextPrimary.G, p.TextPrimary.B), 0.42));
        sweep.Freeze();
        Application.Current.Resources[SpecularSweep] = sweep;
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Radial falloff for one aurora glow (color → transparent at the reach).</summary>
    private static RadialGradientBrush Glow(Color color, double cx, double cy, double rx, double ry, double reach)
    {
        var transparent = Color.FromArgb(0, color.R, color.G, color.B);
        var brush = new RadialGradientBrush(color, transparent);
        brush.GradientStops[1].Offset = reach;
        brush.Center = new Point(cx, cy);
        brush.GradientOrigin = new Point(cx, cy);
        brush.RadiusX = rx;
        brush.RadiusY = ry;
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Tinted(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 160×160 monochrome noise tile (fixed seed) used by the global grain overlay
    /// that kills the flat-gradient look. Cached as an app-level ImageSource.
    /// </summary>
    private static ImageSource CreateNoiseTile()
    {
        const int size = 160;
        var pixels = new byte[size * size * 4];
        var random = new Random(0x414B49); // "AKI"
        for (var i = 0; i < size * size; i++)
        {
            var v = (byte)random.Next(0xFF);
            pixels[i * 4 + 0] = v;
            pixels[i * 4 + 1] = v;
            pixels[i * 4 + 2] = v;
            pixels[i * 4 + 3] = 0xFF;
        }
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
