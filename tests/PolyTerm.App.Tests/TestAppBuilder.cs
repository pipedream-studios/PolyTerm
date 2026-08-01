using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PolyTerm.App.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// xUnit v3 parallelizes test collections across threads by default, and every [AvaloniaFact]-using
// class without its own [Collection] tag (most of this assembly) gets its own implicit collection -
// so by default many of them race to call HeadlessUnitTestSession.EnsureIsolatedApplication() on
// different OS threads at once. That isolated-app setup (a fresh Dispatcher/Compositor per test) isn't
// safe under real concurrent execution: confirmed in CI via a "Test Case Cleanup Failure" -
// InvalidOperationException "The calling thread cannot access this object because a different thread
// owns it" thrown from Dispatcher.VerifyAccess() deep inside AvaloniaHeadlessPlatform.Initialize() for
// an unrelated test class's setup. Disabling collection parallelization for the whole assembly is the
// documented fix for this class of Avalonia.Headless.XUnit issue.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
