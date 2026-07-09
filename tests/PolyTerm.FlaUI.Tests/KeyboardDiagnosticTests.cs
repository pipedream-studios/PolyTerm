using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FluentAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>Isolates whether synthetic input reaches Avalonia at all (keyboard), separate from the mouse-click investigation in <see cref="MainWindowTests"/>.</summary>
public sealed class KeyboardDiagnosticTests : IClassFixture<PolyTermAppFixture>
{
    private readonly PolyTermAppFixture _fixture;

    public KeyboardDiagnosticTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void TypingIntoQuickConnectBox_ChangesItsText()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        var textBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("QuickConnectInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("QuickConnectInput not found by AutomationId.");

        // SendInput-based typing (Keyboard.Type) delivers to whatever window is the OS foreground at
        // the exact moment of injection — not necessarily this test's target, if something else (e.g.
        // the previous test class's PolyTerm.exe still settling its own teardown) wins a focus race in
        // between. Confirmed flaky specifically when run as part of the full suite, not in isolation —
        // consistent with exactly this kind of race, not a logic bug in the app itself. Reasserting
        // foreground immediately before each attempt, and retrying on failure instead of a single
        // Sleep-then-assert, absorbs that race instead of failing on it.
        var lastText = string.Empty;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Win32ForceForeground.Apply(handle);
            Thread.Sleep(200);
            textBox.Focus();
            Thread.Sleep(200);

            Console.WriteLine($"[attempt {attempt}] target handle={handle:X}, foreground={Win32ForceForeground.CurrentForegroundWindow:X}, isForeground={Win32ForceForeground.IsForeground(handle)}");

            Keyboard.Type("hello-flaui");
            Thread.Sleep(500);

            lastText = textBox.Text;
            if (lastText.Contains("hello-flaui"))
                break;

            textBox.Text = string.Empty; // clear any partial/garbled text before the next attempt
        }

        lastText.Should().Contain("hello-flaui", "typed keyboard input should appear in the focused text box");
    }
}
