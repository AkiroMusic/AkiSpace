using System.Diagnostics;
using AkiSpace.Common;
using AkiSpace.Forms;
using AkiSpace.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AkiSpace;

    static class Program
    {
        /// <summary>The main entry point for the application.</summary>
        [STAThread]
    static void Main(string[] args)
    {
        // --fix-env [--disable-wrapper]: re-launched elevated to apply environment fixes.
        if (args.Length > 0 && args[0].Equals("--fix-env", StringComparison.OrdinalIgnoreCase))
        {
            var alsoDisableWrapper = args.Any(a => a.Equals("--disable-wrapper", StringComparison.OrdinalIgnoreCase));
            RunFixMode(alsoDisableWrapper);
            return;
        }

            // Load fonts FIRST before any UI creation
            FontLoader.LoadAll();

            // COM ActiveX (MSTSC) requires STA. WinForms default + PerMonitorV2 DPI.
            ApplicationConfiguration.Initialize();

            var services = BuildServices();
            using var provider = services.BuildServiceProvider();

            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("AkiSpace");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                logger.LogCritical(e.ExceptionObject as Exception, "AppDomain unhandled exception");
            Application.ThreadException += (_, e) =>
                logger.LogError(e.Exception, "UI thread exception");

            // Initialize theme manager with settings
            var settingsService = provider.GetRequiredService<SettingsService>();
            var themeManager = ThemeManager.Current;
            themeManager.Initialize(loggerFactory.CreateLogger<ThemeManager>(), settingsService);

            try
            {
                var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
                logger.LogInformation("AkiSpace starting (build {Version})", version);
                Application.Run(provider.GetRequiredService<MainForm>());
                logger.LogInformation("AkiSpace exited cleanly");
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Fatal startup/shutdown error");
                MessageBox.Show(
                    $"AkiSpace 遇到致命错误：\n{ex.Message}\n\n详细信息见日志。",
                    "AkiSpace",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

    /// <summary>
    /// Elevated mode: apply environment fixes (registry, firewall, service, child sessions).
    /// Shows a simple console window so the user sees progress without a full WinForms UI.
    /// </summary>
    static void RunFixMode(bool alsoDisableWrapper)
    {
        // Allocate a console for visible output
        AllocConsole();
        Console.Title = "AkiSpace — 环境修复（管理员）";
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=== AkiSpace 环境修复（管理员权限）===");
        Console.ResetColor();
        Console.WriteLine();

        var loggerFactory = LoggerFactory.Create(b => b.AddDebug().SetMinimumLevel(LogLevel.Debug));
        var logger = loggerFactory.CreateLogger("AkiSpace.FixEnv");

        // Build a minimal DI just for the services we need
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ChildSessionManager>();
        services.AddSingleton<EnvironmentVerifier>();
        using var provider = services.BuildServiceProvider();

        var verifier = provider.GetRequiredService<EnvironmentVerifier>();

        Console.WriteLine("正在应用环境修复...");
        Console.WriteLine();

        var results = verifier.ApplyAllFixes(alsoDisableWrapper);
        foreach (var r in results)
        {
            var icon = r.Pass ? "✓" : "✗";
            var color = r.Pass ? ConsoleColor.Green : ConsoleColor.Red;
            Console.ForegroundColor = color;
            Console.Write($"  {icon} ");
            Console.ResetColor();
            Console.WriteLine($"{r.Name}: {r.Detail}");
        }

        Console.WriteLine();
        var failures = results.Count(r => !r.Pass);
        if (failures == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("所有修复已成功应用！");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{failures} 项修复失败，请检查上方详情。");
        }
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("按任意键退出...");
        Console.ReadKey(true);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Core services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ChildSessionManager>();
        services.AddSingleton<EnvironmentVerifier>();
        services.AddSingleton<ProcessLauncher>();
        services.AddSingleton<Input.IRawInputMonitor, Input.RawInputMonitor>();
        services.AddSingleton<Input.CursorCapture>();
        services.AddSingleton<Input.KeyboardHandler>();
        services.AddSingleton<Ipc.PipeServer>();
        services.AddSingleton<Ipc.PipeClient>();
        services.AddSingleton<Input.MouseForwarder>();

        // UI
        services.AddTransient<MainForm>();

        return services;
    }
}
