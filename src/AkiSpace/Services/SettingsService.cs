using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Services;

/// <summary>
/// User-editable application settings, persisted to %APPDATA%\AkiSpace\settings.json.
/// Atomic write (tmp + rename), corrupt-file recovery to defaults.
/// </summary>
public sealed class AppSettings
{
    /// <summary>RDP desktop width for the child session.</summary>
    public int DesktopWidth { get; set; } = 1920;

    /// <summary>RDP desktop height for the child session.</summary>
    public int DesktopHeight { get; set; } = 1080;

    /// <summary>RDP color depth in bits.</summary>
    public int ColorDepth { get; set; } = 32;

    /// <summary>Game Mouse Mode (relative-mouse forwarding) enabled by default.</summary>
    public bool GameMouseModeEnabled { get; set; } = false;

    /// <summary>Redirect audio from the child session to this machine.</summary>
    public bool AudioRedirected { get; set; } = false;

    /// <summary>Scale the RDP view to fit the window (vs 1:1).</summary>
    public bool SmartSizing { get; set; } = true;

    /// <summary>Route system shortcuts (Alt+Tab, Win key) to the remote session.</summary>
    public bool SendSystemShortcutsToRemote { get; set; } = true;

    /// <summary>RDP port (read from registry by default, overridable here).</summary>
    public int RdpPort { get; set; } = 3389;

    /// <summary>Automatically connect on app start.</summary>
    public bool AutoConnect { get; set; } = false;

    /// <summary>When closing the viewer, log off the child session too.</summary>
    public bool LogoffOnExit { get; set; } = true;

    /// <summary>Full path to the program to auto-launch inside the child session (optional).</summary>
    public string? LaunchProgramPath { get; set; }

    /// <summary>Connection mode: StandardRdp (different user) or ChildSession (same user, BetterGI style).</summary>
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.StandardRdp;

    /// <summary>Username for the clone session (standard RDP multi-session mode).</summary>
    public string CloneUsername { get; set; } = "AkiSpaceUser";

    /// <summary>Password for the clone session account.</summary>
    public string ClonePassword { get; set; } = "lb33";

    /// <summary>Minimize to system tray instead of taskbar.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Show CPU/memory usage in status bar.</summary>
    public bool ShowPerformance { get; set; } = true;

    /// <summary>Global hotkey to toggle connect/disconnect (Ctrl+Shift+D).</summary>
    public bool EnableGlobalHotkey { get; set; } = true;
}

/// <summary>Connection mode for the desktop clone.</summary>
public enum ConnectionMode
{
    /// <summary>Standard RDP with a different user account (AkiSpaceUser).</summary>
    StandardRdp,

    /// <summary>Child session mode (same user, BetterGI style). Requires Windows support.</summary>
    ChildSession
}

/// <summary>
/// JSON settings persistence with atomic writes and thread-safe access.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<SettingsService> _logger;
    private AppSettings _settings;

    public SettingsService(ILogger<SettingsService> logger)
        : this(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AkiSpace"))
    {
    }

    /// <summary>Test seam: construct with an explicit base directory.</summary>
    internal SettingsService(ILogger<SettingsService> logger, string baseDirectory)
    {
        Directory.CreateDirectory(baseDirectory);
        _filePath = Path.Combine(baseDirectory, "settings.json");
        _logger = logger;
        _settings = LoadCore();
    }

    /// <summary>Current settings snapshot. Do not mutate directly — use <see cref="Update"/>.</summary>
    public AppSettings Current
    {
        get { lock (_gate) return _settings; }
    }

    /// <summary>Atomically replace the settings and persist to disk.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            var clone = Clone(_settings);
            mutate(clone);
            _settings = clone;
            SaveCore(clone);
        }
    }

    private AppSettings LoadCore()
    {
        if (!File.Exists(_filePath))
            return new AppSettings();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Corrupt settings file at {Path}; using defaults", _filePath);
            return new AppSettings();
        }
    }

    private void SaveCore(AppSettings settings)
    {
        var tmpPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private static AppSettings Clone(AppSettings src)
    {
        // JSON round-trip provides a reliable deep clone for our small settings object.
        var json = JsonSerializer.Serialize(src, JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }
}