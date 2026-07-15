using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using PolyTerm.App.McpBridge;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.App.Views;
using PolyTerm.Engine.Credentials;

namespace PolyTerm.App;

/// <summary>Avalonia application entry point: builds the DI container, applies the saved theme, and starts the MCP bridge.</summary>
public sealed partial class App : Application
{
    /// <summary>DI container for the whole app, built in <see cref="OnFrameworkInitializationCompleted"/> once the desktop lifetime is known.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>App settings loaded once from disk at process start (see <see cref="AppSettings.Load"/>).</summary>
    internal static AppSettings Settings { get; } = AppSettings.Load();

    /// <summary>Loads the app's XAML (App.axaml).</summary>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Applies the saved theme, builds the service provider, starts the MCP named-pipe bridge,
    /// and shows the main window (desktop lifetime only).
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme(Settings.ThemeVariant);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services = AppCompositionRoot.BuildServiceProvider();

            // Start the MCP named-pipe server so polyterm-mcp.exe can connect
            Services.GetRequiredService<McpBridgeService>().Start();

            // Start USB COM port polling before MainViewModel/ConnectionTreeViewModel are constructed,
            // so the sidebar's first render already has a chance to see IComPortWatcher.CurrentPorts
            // populated (the first poll is async/non-blocking, so this isn't a hard guarantee - it can
            // still pop in a few hundred ms late).
            Services.GetRequiredService<ComPortWatcherService>().Start();

            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };

            desktop.ShutdownRequested += (_, _) =>
            {
                Services.GetRequiredService<McpBridgeService>().Stop();
                Services.GetRequiredService<ComPortWatcherService>().Stop();
                // Only registered when VaultProvider is "Bitwarden" (see AppCompositionRoot) — stop the
                // managed 'bw serve' child process, if the user logged in via VaultSettingsWindow this
                // session, so it doesn't outlive PolyTerm as an orphaned process (issue #78).
                Services.GetService<BitwardenCliController>()?.StopServe();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Sets the app's requested theme variant. Accepts "Light", "Dark", or anything else for system default.</summary>
    public static void ApplyTheme(string variant)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = variant switch
        {
            "Light" => ThemeVariant.Light,
            "Dark"  => ThemeVariant.Dark,
            _       => ThemeVariant.Default,
        };
    }
}
