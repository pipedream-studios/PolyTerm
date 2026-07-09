using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PolyTerm.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace PolyTerm.App.Tests;

/// <summary>
/// Headless Avalonia app entry point for <c>Avalonia.Headless.XUnit</c>'s <c>[AvaloniaFact]</c> tests
/// (see <see cref="ConfirmDialogTests"/>). Deliberately a plain <see cref="Application"/>, not
/// PolyTerm's own <c>App</c> class — that one builds the full DI container and starts the MCP pipe
/// server in <c>OnFrameworkInitializationCompleted</c>, which is unwanted weight/side effects for
/// isolated dialog/window tests.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
