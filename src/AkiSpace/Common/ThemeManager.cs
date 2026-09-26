using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Common;

/// <summary>
/// Central theme state: resolves the persisted color pack, re-resolves on switch,
/// and exposes the current pack. UI code consumes tokens through the theme host's
/// resource keys — never hardcoded colors.
/// </summary>
public sealed class ThemeManager
{
    private static readonly Lazy<ThemeManager> _instance = new(() => new ThemeManager());
    public static ThemeManager Current => _instance.Value;

    private readonly object _gate = new();
    private ThemePalette _palette;
    private ILogger? _logger;
    private SettingsService? _settingsService;

    public ThemePalette Palette { get { lock (_gate) return _palette; } }
    public string CurrentThemeName { get; private set; } = "dark";

    private ThemeManager()
    {
        _palette = ThemeTokens.Palettes["dark"];
    }

    /// <summary>Resolves the persisted theme and starts tracking switches.</summary>
    public void Initialize(ILogger<ThemeManager> logger, SettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;
        SetTheme(settingsService.Current.Theme ?? "dark", persist: false);
    }

    /// <summary>Switches the active color pack. Unknown names fall back to dark.</summary>
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

            _palette = palette;
            CurrentThemeName = themeName;

            if (persist && _settingsService != null)
            {
                _settingsService.Update(s => s.Theme = themeName);
            }

            _logger?.LogInformation("Theme changed to {Theme}", themeName);
            ThemeChanged?.Invoke(this, themeName);
        }
    }

    /// <summary>Raised when the theme changes — the theme host reapplies resources.</summary>
    public event EventHandler<string>? ThemeChanged;
}
