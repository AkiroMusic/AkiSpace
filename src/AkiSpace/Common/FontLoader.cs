using System.Drawing;
using System.Drawing.Text;
using System.Reflection;

namespace AkiSpace.Common;

/// <summary>
/// Loads local woff2 fonts via PrivateFontCollection at startup.
/// Fonts must be placed in Assets/Fonts/ as embedded resources or copied to output.
/// </summary>
public static class FontLoader
{
    private static readonly PrivateFontCollection _fonts = new();
    private static bool _loaded = false;
    private static readonly object _gate = new();

    /// <summary>
    /// Loads all three font families from embedded resources.
    /// Call once at application startup (Program.cs).
    /// </summary>
    public static void LoadAll()
    {
        lock (_gate)
        {
            if (_loaded) return;

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var fontResources = new[]
                {
                    "AkiSpace.Assets.Fonts.PlusJakartaSans.woff2",
                    "AkiSpace.Assets.Fonts.PlusJakartaSans-Italic.woff2",
                    "AkiSpace.Assets.Fonts.Fraunces.woff2",
                    "AkiSpace.Assets.Fonts.IBMPlexMono.woff2",
                };

                foreach (var resourceName in fontResources)
                {
                    using var stream = asm.GetManifestResourceStream(resourceName);
                    if (stream == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Font resource not found: {resourceName}");
                        continue;
                    }

                    var buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);
                    var ptr = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(buffer.Length);
                    System.Runtime.InteropServices.Marshal.Copy(buffer, 0, ptr, buffer.Length);
                    _fonts.AddMemoryFont(ptr, buffer.Length);
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(ptr);
                }

                _loaded = true;
                System.Diagnostics.Debug.WriteLine($"Loaded {_fonts.Families.Length} font families");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Font loading failed: {ex.Message}");
                // Fallback to system fonts handled by ThemeManager.GetFont()
            }
        }
    }

    /// <summary>
    /// Gets a font family by name, with fallback to system fonts.
    /// </summary>
    public static FontFamily GetFamily(string name)
    {
        if (!_loaded) LoadAll();

        var family = _fonts.Families.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (family != null) return family;

        // Fallback to system font
        try { return new FontFamily(name); }
        catch { return FontFamily.GenericSansSerif; }
    }

    /// <summary>
    /// Creates a font with the loaded family, size, style, and fallbacks.
    /// </summary>
    public static Font CreateFont(string familyName, float size, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Pixel)
    {
        var family = GetFamily(familyName);
        try { return new Font(family, size, style, unit); }
        catch { return new Font(FontFamily.GenericSansSerif, size, style, unit); }
    }

    /// <summary>
    /// UI Sans: Plus Jakarta Sans (200-800)
    /// </summary>
    public static Font UISans(float size, FontStyle style = FontStyle.Regular) =>
        CreateFont(ThemeTokens.Typography.Sans, size, style);

    /// <summary>
    /// Display Serif: Fraunces (100-900)
    /// </summary>
    public static Font Display(float size, FontStyle style = FontStyle.Regular) =>
        CreateFont(ThemeTokens.Typography.Display, size, style);

    /// <summary>
    /// Mono: IBM Plex Mono (400-700)
    /// </summary>
    public static Font Mono(float size, FontStyle style = FontStyle.Regular) =>
        CreateFont(ThemeTokens.Typography.Mono, size, style);

    /// <summary>
    /// All loaded font families for debugging.
    /// </summary>
    public static IReadOnlyList<FontFamily> Families => _fonts.Families;
}