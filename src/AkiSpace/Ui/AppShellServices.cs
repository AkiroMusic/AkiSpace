using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Ui;

/// <summary>
/// Static access point for the services the WPF windows need (set once by the
/// shell at startup). Keeps window constructors parameter-light without a
/// full-blown MVVM locator.
/// </summary>
internal static class AppShellServices
{
    private static ILoggerFactory? _loggerFactory;

    public static SettingsService Settings { get; private set; } = null!;
    public static EnvironmentVerifier EnvironmentVerifier { get; private set; } = null!;
    public static ChildSessionManager ChildSessionManager { get; private set; } = null!;

    public static void Init(
        ILoggerFactory loggerFactory,
        SettingsService settings,
        EnvironmentVerifier environmentVerifier,
        ChildSessionManager childSessionManager)
    {
        _loggerFactory = loggerFactory;
        Settings = settings;
        EnvironmentVerifier = environmentVerifier;
        ChildSessionManager = childSessionManager;
    }

    public static ILogger<T> LoggerFor<T>() =>
        (_loggerFactory ?? throw new InvalidOperationException("AppShellServices not initialized"))
            .CreateLogger<T>();
}
