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

        textBox.Focus();
        Thread.Sleep(200);
        Keyboard.Type("hello-flaui");
        Thread.Sleep(500);

        textBox.Text.Should().Contain("hello-flaui", "typed keyboard input should appear in the focused text box");
    }
}
