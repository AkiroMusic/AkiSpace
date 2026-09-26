using System.Windows;
using System.Windows.Media;
using AkiSpace.Common;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace AkiSpace.Ui.Theme;

/// <summary>
/// Bridges the Common ThemeManager palettes into WPF application resources.
/// Colors live under the "Brush.*" keys that Styles.xaml references via
/// DynamicResource; <see cref="Apply"/> rebuilds them so a ThemeManager theme
/// switch restyles every open window with zero custom paint code.
/// </summary>
public static class WpfThemeHost
{
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
    public const string Success = "Brush.Success";
    public const string Error = "Brush.Error";
    public const string Warning = "Brush.Warning";

    /// <summary>Materializes the current palette and starts tracking theme changes.</summary>
    public static void Initialize()
    {
        Apply();
        ThemeManager.Current.ThemeChanged += (_, _) => Apply();
    }

    public static void Apply()
    {
        var t = ThemeManager.Current;
        var dict = new ResourceDictionary
        {
            [BgBase] = Brush(t.BgBase),
            [Surface1] = Brush(t.Surface1),
            [Surface2] = Brush(t.Surface2),
            [Border] = Brush(t.Border),
            [TextPrimary] = Brush(t.TextPrimary),
            [TextSecondary] = Brush(t.TextSecondary),
            [TextTertiary] = Brush(t.TextTertiary),
            [Accent] = Brush(t.Accent),
            [AccentHover] = Brush(t.AccentHover),
            [AccentSecondary] = Brush(t.AccentSecondary),
            [Success] = Brush(t.Success),
            [Error] = Brush(t.Error),
            [Warning] = Brush(t.Warning),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        // Styles dictionary first (loaded once), then the palette — merged lookup
        // resolves DynamicResource against every dictionary in order.
        var styles = merged.FirstOrDefault(d => (string?)d["Meta.Kind"] == "styles");
        merged.Clear();
        if (styles is not null) merged.Add(styles);
        merged.Add(dict);
    }

    private static SolidColorBrush Brush(System.Drawing.Color color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
