using Avalonia.Input;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Framebuffer;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Surfaces.Tests;

/// <summary>
/// Covers <see cref="FramebufferSurface"/>'s pure pointer-coordinate/key-mapping logic (issue #66) —
/// the class itself is an Avalonia <c>Control</c> that needs a real render pass to construct/attach
/// meaningfully, but this specific logic (coordinate scaling/clamping, X11 keysym encoding, pointer
/// button/modifier translation) has none of that dependency, so it's exposed as <c>internal static</c>
/// (via <c>InternalsVisibleTo</c>) and tested directly — the same class of bug (subtle parsing/mapping
/// mistakes) that <c>X11DisplayEndpointTests</c> and <c>VncPixelFormatTests</c> already found real,
/// shipped bugs in elsewhere in this codebase.
/// </summary>
public sealed class FramebufferSurfaceTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(99, 99, 99, 99)]  // last valid pixel index for a 100x100 channel (0..99)
    [InlineData(50, 25, 50, 25)]  // midpoint, 1:1 scale
    public void ComputeRemoteCoords_ScalesOneToOne_WhenBoundsMatchChannelSize(double px, double py, int expectedX, int expectedY)
    {
        var (x, y) = FramebufferSurface.ComputeRemoteCoords(px, py, boundsWidth: 100, boundsHeight: 100, channelWidth: 100, channelHeight: 100);

        x.Should().Be(expectedX);
        y.Should().Be(expectedY);
    }

    [Fact]
    public void ComputeRemoteCoords_ScalesProportionally_WhenSurfaceIsSmallerThanChannel()
    {
        // A 200x100 remote framebuffer rendered into a 100x50 (half-size) local control: the
        // pointer's own local midpoint (50,25) must map to the framebuffer's midpoint (100,50), not
        // its own local pixel coordinates.
        var (x, y) = FramebufferSurface.ComputeRemoteCoords(50, 25, boundsWidth: 100, boundsHeight: 50, channelWidth: 200, channelHeight: 100);

        x.Should().Be(100);
        y.Should().Be(50);
    }

    [Fact]
    public void ComputeRemoteCoords_ClampsToChannelBounds_WhenPointerIsPastTheEdge()
    {
        // A pointer position at/just past the control's own reported bounds must never map to a
        // remote coordinate outside the channel's own dimensions — off-by-one here would silently
        // misplace every click at the framebuffer's edges.
        var (x, y) = FramebufferSurface.ComputeRemoteCoords(1000, 1000, boundsWidth: 100, boundsHeight: 100, channelWidth: 50, channelHeight: 50);

        x.Should().Be(49);
        y.Should().Be(49);
    }

    [Theory]
    [InlineData(0, 0)]     // zero bounds
    [InlineData(-1, 100)]  // negative bounds (shouldn't happen, but must not throw/misbehave)
    public void ComputeRemoteCoords_ReturnsOrigin_WhenBoundsAreZeroOrNegative(double boundsWidth, double boundsHeight)
    {
        var (x, y) = FramebufferSurface.ComputeRemoteCoords(10, 10, boundsWidth, boundsHeight, channelWidth: 100, channelHeight: 100);

        x.Should().Be(0);
        y.Should().Be(0);
    }

    [Fact]
    public void ComputeRemoteCoords_ReturnsOrigin_WhenChannelSizeIsZero()
    {
        var (x, y) = FramebufferSurface.ComputeRemoteCoords(10, 10, boundsWidth: 100, boundsHeight: 100, channelWidth: 0, channelHeight: 0);

        x.Should().Be(0);
        y.Should().Be(0);
    }

    [Theory]
    [InlineData('A', "65")]      // Latin-1 range: keysym == code point, per X11 keysymdef.h
    [InlineData(' ', "32")]
    [InlineData((char)0xFF, "255")] // top of the Latin-1 range
    public void CharToKeySymbol_Latin1Range_MapsDirectlyToCodePoint(char c, string expected) =>
        FramebufferSurface.CharToKeySymbol(c).Should().Be(expected);

    [Fact]
    public void CharToKeySymbol_BeyondLatin1_UsesTheX11UnicodeKeysymOffset()
    {
        // X11 keysymdef.h convention for any code point beyond Latin-1: 0x01000000 + code point.
        const char c = '€'; // U+20AC, well beyond 0xFF
        var expected = (0x01000000 + '€').ToString(System.Globalization.CultureInfo.InvariantCulture);

        FramebufferSurface.CharToKeySymbol(c).Should().Be(expected);
    }

    [Theory]
    [InlineData(PointerUpdateKind.LeftButtonPressed, PointerButton.Left)]
    [InlineData(PointerUpdateKind.LeftButtonReleased, PointerButton.Left)]
    [InlineData(PointerUpdateKind.RightButtonPressed, PointerButton.Right)]
    [InlineData(PointerUpdateKind.RightButtonReleased, PointerButton.Right)]
    [InlineData(PointerUpdateKind.MiddleButtonPressed, PointerButton.Middle)]
    [InlineData(PointerUpdateKind.MiddleButtonReleased, PointerButton.Middle)]
    [InlineData(PointerUpdateKind.Other, PointerButton.None)]
    public void ToPointerButton_MapsEachUpdateKind(PointerUpdateKind kind, PointerButton expected) =>
        FramebufferSurface.ToPointerButton(kind).Should().Be(expected);

    [Theory]
    [InlineData(KeyModifiers.None, ModifierKeys.None)]
    [InlineData(KeyModifiers.Shift, ModifierKeys.Shift)]
    [InlineData(KeyModifiers.Control, ModifierKeys.Control)]
    [InlineData(KeyModifiers.Alt, ModifierKeys.Alt)]
    [InlineData(KeyModifiers.Meta, ModifierKeys.Meta)]
    [InlineData(KeyModifiers.Shift | KeyModifiers.Control, ModifierKeys.Shift | ModifierKeys.Control)]
    [InlineData(KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta,
                ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Meta)]
    public void ToModifiers_CombinesEveryHeldModifierFlag(KeyModifiers input, ModifierKeys expected) =>
        FramebufferSurface.ToModifiers(input).Should().Be(expected);

    [Theory]
    [InlineData(Key.Enter, "Return")]
    [InlineData(Key.Back, "BackSpace")]
    [InlineData(Key.Escape, "Escape")]
    [InlineData(Key.Up, "Up")]
    [InlineData(Key.Down, "Down")]
    [InlineData(Key.PageUp, "Prior")]   // X11 keysym name, not "PageUp"
    [InlineData(Key.PageDown, "Next")]  // X11 keysym name, not "PageDown"
    [InlineData(Key.LeftCtrl, "Control_L")]
    [InlineData(Key.RightCtrl, "Control_R")]
    [InlineData(Key.LWin, "Super_L")]
    [InlineData(Key.F1, "F1")]
    [InlineData(Key.F12, "F12")]
    public void ControlKeyMap_MapsExpectedX11KeysymNames(Key key, string expectedSymbol)
    {
        FramebufferSurface.ControlKeyMap.Should().ContainKey(key);
        FramebufferSurface.ControlKeyMap[key].Should().Be(expectedSymbol);
    }

    [Fact]
    public void ControlKeyMap_DoesNotMapPrintableCharacterKeys()
    {
        // Printable characters go through OnTextInput/CharToKeySymbol instead — if a letter key ever
        // ends up in this map too, SendControlKey would fire a second, spurious key event alongside
        // OnTextInput's own press+release pair for the same keystroke.
        FramebufferSurface.ControlKeyMap.Should().NotContainKey(Key.A);
    }
}
