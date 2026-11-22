namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Base for test classes that drive the shared, launched app (<see cref="PolyTermAppFixture"/>).
/// xUnit creates and disposes a test class instance around every test, so:
/// <list type="bullet">
/// <item>the constructor resets the app to just its main window, so a test that failed with a
/// dialog open can't fail the next one too (issue #204);</item>
/// <item><see cref="Dispose"/> saves a screenshot and UIA tree dump if the test failed (issue
/// #206).</item>
/// </list>
/// </summary>
public abstract class PolyTermUiTest : IDisposable
{
    private readonly PolyTermAppFixture _appFixture;

    protected PolyTermUiTest(PolyTermAppFixture fixture)
    {
        _appFixture = fixture;
        fixture.ResetToMainWindow();
    }

    public void Dispose()
    {
        FailureDiagnostics.CaptureIfFailed(_appFixture);
        GC.SuppressFinalize(this);
    }
}
