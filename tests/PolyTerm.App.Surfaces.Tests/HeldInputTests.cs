using Avalonia.Input;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Framebuffer;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Surfaces.Tests;

/// <summary>Covers the held-key and held-button bookkeeping behind issue #368; <c>FramebufferSurfaceInputTests</c> in PolyTerm.App.Tests covers when the surface acts on it.</summary>
public sealed class HeldInputTests
{
    private static readonly KeyModifiers All =
        KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;

    [Fact]
    public void TakeKeys_ReturnsHeldKeysNewestFirst_AndForgetsThem()
    {
        var held = new HeldInput();
        held.Press(Key.LWin);
        held.Press(Key.LeftShift);

        held.TakeKeys().Should().Equal(Key.LeftShift, Key.LWin);
        held.TakeKeys().Should().BeEmpty();
    }

    [Fact]
    public void TakeKeys_ReturnsAnAutoRepeatedKeyOnce()
    {
        var held = new HeldInput();
        held.Press(Key.Down);
        held.Press(Key.Down);

        held.TakeKeys().Should().Equal(Key.Down);
    }

    [Fact]
    public void TakeKeys_LeavesOutReleasedKeys()
    {
        var held = new HeldInput();
        held.Press(Key.LWin);
        held.Press(Key.Tab);
        held.Release(Key.Tab);

        held.TakeKeys().Should().Equal(Key.LWin);
    }

    [Fact]
    public void TakeButtons_ReturnsOnlyButtonsStillHeld()
    {
        var held = new HeldInput();
        held.Press(PointerButton.Left);
        held.Press(PointerButton.Right);
        held.Release(PointerButton.Left);

        held.TakeButtons().Should().Equal(PointerButton.Right);
        held.TakeButtons().Should().BeEmpty();
    }

    [Fact]
    public void TakeModifiersNotDown_ReturnsOnlyTheModifiersNoLongerReported()
    {
        var held = new HeldInput();
        held.Press(Key.LWin);
        held.Press(Key.LeftShift);
        held.Press(Key.Return);

        held.TakeModifiersNotDown(KeyModifiers.Shift, All).Should().Equal(Key.LWin);
        held.TakeKeys().Should().Equal(Key.Return, Key.LeftShift);
    }

    [Fact]
    public void TakeModifiersNotDown_ReturnsBothKeysOfAPair_OnlyOnceNeitherIsDown()
    {
        var held = new HeldInput();
        held.Press(Key.LeftShift);
        held.Press(Key.RightShift);

        held.TakeModifiersNotDown(KeyModifiers.Shift, All).Should().BeEmpty();
        held.TakeModifiersNotDown(KeyModifiers.None, All).Should().Equal(Key.RightShift, Key.LeftShift);
    }

    [Fact]
    public void TakeModifiersNotDown_LeavesModifiersThatCannotBeJudged()
    {
        var held = new HeldInput();
        held.Press(Key.RightAlt);

        held.TakeModifiersNotDown(KeyModifiers.None, All & ~KeyModifiers.Alt).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ReliableModifiers_IncludesAltOnlyOnMacOS(bool isMacOS, bool includesAlt)
    {
        var reliable = HeldInput.ReliableModifiers(isMacOS);

        reliable.HasFlag(KeyModifiers.Alt).Should().Be(includesAlt);
        reliable.Should().HaveFlag(KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Meta);
    }

    [Theory]
    [InlineData(Key.LeftShift, KeyModifiers.Shift)]
    [InlineData(Key.RightShift, KeyModifiers.Shift)]
    [InlineData(Key.LeftCtrl, KeyModifiers.Control)]
    [InlineData(Key.RightCtrl, KeyModifiers.Control)]
    [InlineData(Key.LeftAlt, KeyModifiers.Alt)]
    [InlineData(Key.RightAlt, KeyModifiers.Alt)]
    [InlineData(Key.LWin, KeyModifiers.Meta)]
    [InlineData(Key.RWin, KeyModifiers.Meta)]
    [InlineData(Key.CapsLock, KeyModifiers.None)]
    [InlineData(Key.Return, KeyModifiers.None)]
    public void ModifierFor_MapsBothKeysOfEachModifier(Key key, KeyModifiers expected) =>
        HeldInput.ModifierFor(key).Should().Be(expected);
}
