using AwesomeAssertions;
using PolyTerm.Engine;
using PolyTerm.Protocols.Rdp;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Drives <see cref="Win32NativeWindowInput"/> (extracted from <c>RdpNativeHostChannel</c> so it's
/// testable independent of a live RDP session/MSTSC control) against a real, throwaway WinForms
/// window — proving the actual Win32 capture/message-posting mechanics work against a real HWND. This
/// does NOT prove the MSTSC ActiveX control itself reacts correctly to injected input (no real RDP
/// session available in this environment — see docs/todos.md); it proves the interop layer underneath
/// it is sound against something real, not just reviewed code.
/// </summary>
public sealed class Win32NativeWindowInputTests : IDisposable
{
    private readonly System.Windows.Forms.Form _form;

    public Win32NativeWindowInputTests()
    {
        _form = new System.Windows.Forms.Form
        {
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Location = new System.Drawing.Point(-2000, -2000), // off-screen, not actually visible
            Size = new System.Drawing.Size(320, 240),
            ShowInTaskbar = false,
        };
        _form.Show();
    }

    public void Dispose() => _form.Dispose();

    [Fact]
    public void CaptureWindow_ReturnsPixelData_MatchingTheRealWindowsClientSize()
    {
        var result = Win32NativeWindowInput.CaptureWindow(_form.Handle);

        result.Should().NotBeNull("a real, shown window should capture successfully");
        result!.Value.Width.Should().Be(_form.ClientSize.Width);
        result.Value.Height.Should().Be(_form.ClientSize.Height);
        result.Value.Data.Should().HaveCount(result.Value.Width * result.Value.Height * 4, "BGRA32 is 4 bytes per pixel");
    }

    [Fact]
    public void CaptureWindow_ReturnsNull_ForAZeroHandle()
    {
        Win32NativeWindowInput.CaptureWindow(0).Should().BeNull();
    }

    [Fact]
    public void SendPointer_DoesNotThrow_AgainstARealWindow()
    {
        var act = () =>
        {
            Win32NativeWindowInput.SendPointer(_form.Handle, 10, 10, PointerButton.Left, PointerEventKind.Press);
            Win32NativeWindowInput.SendPointer(_form.Handle, 10, 10, PointerButton.Left, PointerEventKind.Release);
            Win32NativeWindowInput.SendPointer(_form.Handle, 20, 20, PointerButton.None, PointerEventKind.Move);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void SendKey_DoesNotThrow_ForALiteralCharacter_AgainstARealWindow()
    {
        var act = () => Win32NativeWindowInput.SendKey(_form.Handle, "a", KeyEventKind.Press);

        act.Should().NotThrow();
    }

    [Fact]
    public void SendKey_DoesNotThrow_ForANamedKey_AgainstARealWindow()
    {
        var act = () =>
        {
            Win32NativeWindowInput.SendKey(_form.Handle, "Return", KeyEventKind.Press);
            Win32NativeWindowInput.SendKey(_form.Handle, "Return", KeyEventKind.Release);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void SendKey_IsANoOp_ForAnUnrecognizedNamedKey()
    {
        var act = () => Win32NativeWindowInput.SendKey(_form.Handle, "NotARealKeyName", KeyEventKind.Press);

        act.Should().NotThrow("an unrecognized key name should be silently ignored, not throw");
    }

    [Theory]
    [InlineData("Return", 0x0D)]
    [InlineData("Escape", 0x1B)]
    [InlineData("Tab", 0x09)]
    [InlineData("BackSpace", 0x08)]
    [InlineData("Delete", 0x2E)]
    [InlineData("Up", 0x26)]
    [InlineData("F5", 0x74)]
    public void NamedKeyToVirtualKey_MapsKnownXKeysymNames_ToTheCorrectWindowsVirtualKeyCode(string name, int expectedVk)
    {
        Win32NativeWindowInput.NamedKeyToVirtualKey.Should().ContainKey(name).WhoseValue.Should().Be(expectedVk);
    }
}
