using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using PolyTerm.App.McpBridge;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.App.Views;
using PolyTerm.Engine.Credentials;
using PolyTerm.Protocols.Ssh.X11;

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
        ApplyTerminalFontResource();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services = AppCompositionRoot.BuildServiceProvider();

            // Bitwarden login persistence: fire-and-forget, never blocks startup - TryAutoUnlockAsync
            // itself never throws (missing keychain entry, revoked refresh token, unsupported platform
            // all just return false), and is a no-op with zero network calls when there's no persisted
            // "Remember this device" entry to try in the first place. BitwardenApiClient is only
            // registered when Bitwarden is the active saved provider (see AppCompositionRoot), so
            // GetService (not GetRequiredService) is deliberate here.
            if (Services.GetService<BitwardenApiClient>() is { } bitwarden && Settings.BitwardenLastEmail is { Length: > 0 } lastEmail)
                _ = bitwarden.TryAutoUnlockAsync(lastEmail);

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

            if (OperatingSystem.IsMacOS())
                InstallMacAppMenu(this);

            desktop.ShutdownRequested += async (_, _) =>
            {
                // Cancels the bridge's pipe loops right away; the Exit handler below waits for them.
                _ = Services.GetRequiredService<McpBridgeService>().StopAsync();
                Services.GetRequiredService<ComPortWatcherService>().Stop();
                // Same explicit lifecycle convention as the Stop() calls above - stops PolyTerm's
                // own bundled X server (issue #76) if one was ever started this run. A no-op on every
                // platform except Windows (NullBundledX11ServerProvider.StopAsync()).
                await Services.GetRequiredService<IBundledX11ServerProvider>().StopAsync();
            };

            // ShutdownRequested's async handler isn't awaited by Avalonia, so the process can exit
            // before the bridge's pipe loops dispose their pipes. On macOS/Linux that would leave a
            // CoreFxPipe_polyterm socket file in the temp directory (issue #226). Exit runs
            // synchronously before Main returns; Dispose() waits (bounded) for the loops to finish.
            desktop.Exit += (_, _) => Services.GetRequiredService<McpBridgeService>().Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Fills the macOS app menu (the one titled "PolyTerm", issue #307) with About PolyTerm. Avalonia
    /// appends the standard Services, Hide, Hide Others, Show All and Quit (⌘Q) items after it.
    /// </summary>
    /// <remarks>
    /// By this point Avalonia has already put its own default app menu (with "About Avalonia", which
    /// opens Avalonia's About dialog) on the Application, and its menu exporter doesn't pick up a
    /// replacement menu set afterwards. It does follow changes to the menu's items, so only the About
    /// item of the existing menu is swapped. The rest of that menu is Avalonia's Services, Hide,
    /// Show All and Quit items and must stay. A new menu is only set when none exists yet.
    /// </remarks>
    internal static void InstallMacAppMenu(Application app)
    {
        var about = new NativeMenuItem("About PolyTerm");
        about.Click += (_, _) =>
        {
            if ((app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is { } owner)
                _ = new AboutWindow().ShowDialog(owner);
        };

        if (NativeMenu.GetMenu(app) is { } existing)
        {
            var avaloniaAbout = existing.Items.OfType<NativeMenuItem>()
                .FirstOrDefault(i => i.Header?.StartsWith("About ", StringComparison.Ordinal) == true);
            if (avaloniaAbout is not null)
                existing.Items[existing.Items.IndexOf(avaloniaAbout)] = about;
            else
                existing.Items.Insert(0, about);
        }
        else
        {
            NativeMenu.SetMenu(app, new NativeMenu { Items = { about } });
        }
    }

    /// <summary>Raised after <see cref="SetTerminalAppearance"/> changes terminal appearance, so open terminal tabs can pick it up.</summary>
    internal static event EventHandler? TerminalAppearanceChanged;

    /// <summary>Bumped by every <see cref="SetTerminalAppearance"/> call, so a surface can tell whether it already has the current appearance.</summary>
    internal static int TerminalAppearanceVersion { get; private set; }

    /// <summary>App resource key for the terminal font family, used by the session tab headers.</summary>
    internal const string TerminalFontResourceKey = "TerminalFontFamily";

    /// <summary>
    /// Persists the terminal appearance (null family/scheme = per-OS default font / default scheme) and applies it
    /// to every open terminal and tab header.
    /// </summary>
    internal static void SetTerminalAppearance(string? family, double size, string? colorScheme, string cursorStyle)
    {
        Settings.TerminalFontFamily  = family;
        Settings.TerminalFontSize    = size;
        Settings.TerminalColorScheme = colorScheme;
        Settings.TerminalCursorStyle = cursorStyle;
        Settings.Save();
        ApplyTerminalFontResource();
        TerminalAppearanceVersion++;
        TerminalAppearanceChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Raised after <see cref="SetTerminalCopyOnSelect"/>, so open terminal tabs can pick the setting up.</summary>
    internal static event EventHandler? TerminalCopyOnSelectChanged;

    /// <summary>
    /// Persists the copy-on-select setting and applies it to every open terminal. Separate from
    /// <see cref="TerminalAppearanceChanged"/>, which re-applies the saved font size and would undo a tab's own zoom.
    /// </summary>
    internal static void SetTerminalCopyOnSelect(bool enabled)
    {
        Settings.TerminalCopyOnSelect = enabled;
        Settings.Save();
        TerminalCopyOnSelectChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Publishes the resolved terminal font as <see cref="TerminalFontResourceKey"/> so tab headers can bind it with DynamicResource.</summary>
    internal static void ApplyTerminalFontResource()
    {
        if (Current is null) return;
        Current.Resources[TerminalFontResourceKey] = PolyTerm.App.Surfaces.Terminal.TerminalFonts.Resolve(Settings.TerminalFontFamily);
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
