using Avalonia;

namespace PolyTerm.App;

/// <summary>Process entry point for the Avalonia desktop app.</summary>
internal static class Program
{
    /// <summary>Starts the app with the classic desktop lifetime. STAThread is required for platform interop (e.g. clipboard, file dialogs) on Windows.</summary>
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Configures the Avalonia <see cref="App"/> with platform auto-detection, the Inter font, and trace logging.</summary>
    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
