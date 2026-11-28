using Avalonia;
using Avalonia.Headless;
using PolyTerm.App.Surfaces.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// Avalonia's headless setup isn't safe to run from several test collections at once - see the same
// attribute in PolyTerm.App.Tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PolyTerm.App.Surfaces.Tests;

/// <summary>
/// Headless app for this assembly's <c>[AvaloniaFact]</c> tests. It renders with Skia, unlike the one in
/// PolyTerm.App.Tests, because the default headless renderer throws away what's written to a bitmap, so a
/// test couldn't tell what a surface shows.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
