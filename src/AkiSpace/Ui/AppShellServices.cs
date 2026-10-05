using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Ui;

/// <summary>
/// Services shared by the WPF windows, set once by the shell at startup.
/// </summary>
internal static class AppShellServices
{
    private static ILoggerFactory? _loggerFactory;

    public static SettingsService Settings { get; private set; } = null!;
    public static EnvironmentVerifier EnvironmentVerifier { get; private set; } = null!;
    public static ChildSessionManager ChildSessionManager { get; private set; } = null!;
    public static RdpWrapperInstaller WrapperInstaller { get; private set; } = null!;

    public static void Init(
        ILoggerFactory loggerFactory,
        SettingsService settings,
        EnvironmentVerifier environmentVerifier,
        ChildSessionManager childSessionManager,
        RdpWrapperInstaller wrapperInstaller)
    {
        _loggerFactory = loggerFactory;
        Settings = settings;
        EnvironmentVerifier = environmentVerifier;
        ChildSessionManager = childSessionManager;
        WrapperInstaller = wrapperInstaller;
    }

    public static ILogger<T> LoggerFor<T>() =>
        (_loggerFactory ?? throw new InvalidOperationException("AppShellServices not initialized"))
            .CreateLogger<T>();
}
