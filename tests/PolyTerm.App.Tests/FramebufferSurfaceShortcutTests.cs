using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Framebuffer;
using PolyTerm.Protocols.RdpFreerdp;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Issue #398: a shortcut on a character key, such as Ctrl+C, has to reach the remote as the key
/// itself, as no text is typed for it. Drives a live surface in a headless window. A key-down that's
/// marked handled types no text on any platform, so that's what shows a key can't arrive twice. Each
/// test says whose keyboard rules it follows, as they differ and the tests run on every OS.
/// </summary>
public sealed class FramebufferSurfaceShortcutTests
{
    [AvaloniaTheory]
    [InlineData(nameof(KeyboardHost.Windows))]
    [InlineData(nameof(KeyboardHost.MacOS))]
    [InlineData(nameof(KeyboardHost.Linux))]
    public void CtrlC_SendsTheKeyBetweenCtrlsPressAndRelease_AndTypesNothing(string host)
    {
        var shown = Show(Enum.Parse<KeyboardHost>(host));

        shown.Window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        shown.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        shown.Window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        shown.Window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);

        shown.Channel.Sent.Should().Equal(
            "press Control_L", "press key C 'c'", "release key C 'c'", "release Control_L");
        shown.Handled.Should().Equal(true, true);
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void AKeyWithoutAModifier_IsLeftToTypeItsText()
    {
        var shown = Show(KeyboardHost.Linux);

        shown.Window.KeyPress(Key.C, RawInputModifiers.None, PhysicalKey.C, "c");
        shown.Window.KeyRelease(Key.C, RawInputModifiers.None, PhysicalKey.C, "c");

        shown.Channel.Sent.Should().BeEmpty();
        shown.Handled.Should().Equal(false);
        shown.Window.KeyTextInput("c");
        shown.Channel.Sent.Should().Equal("press 99", "release 99");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void AltGrOnWindows_IsLeftToTypeItsCharacter()
    {
        var shown = Show(KeyboardHost.Windows);

        shown.Window.KeyPress(Key.Q, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.Q, "@");

        shown.Channel.Sent.Should().BeEmpty();
        shown.Handled.Should().Equal(false);
        shown.Window.Close();
    }

    /// <summary>A Windows host drops typed text while it holds Alt, so it mustn't be told about Option here.</summary>
    [AvaloniaFact]
    public void OptionTypingACharacterOnMacOS_SendsOnlyTheCharacter()
    {
        var shown = Show(KeyboardHost.MacOS);

        shown.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        shown.Window.KeyPress(Key.G, RawInputModifiers.Alt, PhysicalKey.G, "©");
        shown.Window.KeyTextInput("©");
        shown.Window.KeyRelease(Key.G, RawInputModifiers.Alt, PhysicalKey.G, "©");
        shown.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);

        shown.Channel.Sent.Should().Equal("press 169", "release 169");
        shown.Window.Close();
    }

    /// <summary>The first half of an accented letter types nothing, and an Alt tap would move the remote to its menu.</summary>
    [AvaloniaFact]
    public void AnOptionDeadKeyOnMacOS_SendsNothing()
    {
        var shown = Show(KeyboardHost.MacOS);

        shown.Window.KeyPress(Key.RightAlt, RawInputModifiers.Alt, PhysicalKey.AltRight, null);
        shown.Window.KeyPress(Key.E, RawInputModifiers.Alt, PhysicalKey.E, null);
        shown.Window.KeyRelease(Key.E, RawInputModifiers.Alt, PhysicalKey.E, null);
        shown.Window.KeyRelease(Key.RightAlt, RawInputModifiers.None, PhysicalKey.AltRight, null);

        shown.Channel.Sent.Should().BeEmpty();
        shown.Window.Close();
    }

    /// <summary>
    /// What lets macOS join a dead key to the letter after it. The joining is done by macOS, so a
    /// headless window can only show that the surface asks for it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(nameof(KeyboardHost.MacOS), true)]
    [InlineData(nameof(KeyboardHost.Windows), false)]
    [InlineData(nameof(KeyboardHost.Linux), false)]
    public void TakesComposedText_OnMacOSOnly(string host, bool expected)
    {
        var (window, surface, _, _) = FramebufferSurfaceInputTests.Show();
        surface.KeyboardHost = Enum.Parse<KeyboardHost>(host);
        var request = new TextInputMethodClientRequestedEventArgs
        {
            RoutedEvent = InputElement.TextInputMethodClientRequestedEvent,
        };

        surface.RaiseEvent(request);

        (request.Client is not null).Should().Be(expected);
        window.Close();
    }

    [AvaloniaFact]
    public void TappingOptionOnMacOS_TapsAlt()
    {
        var shown = Show(KeyboardHost.MacOS);

        shown.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        shown.Channel.Sent.Should().BeEmpty();
        shown.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);

        shown.Channel.Sent.Should().Equal("press Alt_L", "release Alt_L");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void OptionWithAKeyThatTypesNothing_SendsAlt_AndLetsGoOfItToType()
    {
        var shown = Show(KeyboardHost.MacOS);
        shown.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);

        shown.Window.KeyPress(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null);
        shown.Window.KeyRelease(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null);
        shown.Window.KeyTextInput("©");
        shown.Window.KeyPress(Key.F4, RawInputModifiers.Alt, PhysicalKey.F4, null);
        shown.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);

        shown.Channel.Sent.Should().Equal(
            "press Alt_L", "press Left", "release Left", "release Alt_L", "press 169", "release 169",
            "press Alt_L", "press F4", "release Alt_L");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void AnOptionClickOnMacOS_SendsAltBeforeTheClick()
    {
        var shown = Show(KeyboardHost.MacOS);
        shown.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);

        var middle = new Avalonia.Point(50, 50);
        shown.Window.MouseDown(middle, MouseButton.Left, RawInputModifiers.Alt);
        shown.Window.MouseUp(middle, MouseButton.Left, RawInputModifiers.Alt);
        shown.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);

        shown.Channel.SentExceptMoves.Should().Equal("press Alt_L", "press Left", "release Left", "release Alt_L");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void CtrlOptionShortcutOnMacOS_SendsAltBeforeTheKey()
    {
        var shown = Show(KeyboardHost.MacOS);
        var held = RawInputModifiers.Control | RawInputModifiers.Alt;
        shown.Window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        shown.Window.KeyPress(Key.LeftAlt, held, PhysicalKey.AltLeft, null);

        shown.Window.KeyPress(Key.T, held, PhysicalKey.T, "†");

        shown.Channel.Sent.Should().Equal("press Control_L", "press Alt_L", "press key T 't'");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void LosingTheKeyboardWithOptionDownOnMacOS_SendsNothing()
    {
        var shown = Show(KeyboardHost.MacOS);
        shown.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);

        shown.Other.Focus();

        shown.Channel.Sent.Should().BeEmpty();
        shown.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(nameof(KeyboardHost.Windows))]
    [InlineData(nameof(KeyboardHost.Linux))]
    public void AltElsewhere_IsSentAsItGoesDown(string host)
    {
        var shown = Show(Enum.Parse<KeyboardHost>(host));

        shown.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);

        shown.Channel.Sent.Should().Equal("press Alt_L");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void ReleasingTheModifierFirst_StillReleasesTheKey()
    {
        var shown = Show(KeyboardHost.Windows);
        shown.Window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        shown.Window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);

        shown.Window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        shown.Window.KeyRelease(Key.V, RawInputModifiers.None, PhysicalKey.V, "v");

        shown.Channel.Sent.Should().Equal(
            "press Control_L", "press key V 'v'", "release Control_L", "release key V 'v'");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void ReleasingShiftFirst_ReleasesTheKeyWithTheCharacterItWentDownWith()
    {
        var shown = Show(KeyboardHost.Linux);
        var held = RawInputModifiers.Control | RawInputModifiers.Shift;
        shown.Window.KeyPress(Key.C, held, PhysicalKey.C, "C");

        shown.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c"); // auto-repeat
        shown.Window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");

        shown.Channel.Sent.Should().Equal("press key C 'C'", "press key C 'C'", "release key C 'C'");
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void AutoRepeatAfterTheModifierIsLetGo_ReleasesTheKey()
    {
        var shown = Show(KeyboardHost.Linux);
        shown.Window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");

        shown.Window.KeyPress(Key.Z, RawInputModifiers.None, PhysicalKey.Z, "z");

        shown.Channel.Sent.Should().Equal("press key Z 'z'", "release key Z 'z'");
        shown.Window.Close();
    }

    /// <summary>macOS may not report a key-up while Command is down, so none can be relied on.</summary>
    [AvaloniaFact]
    public void ACommandShortcutOnMacOS_IsPressedAndReleasedAtOnce()
    {
        var shown = Show(KeyboardHost.MacOS);
        shown.Window.KeyPress(Key.LWin, RawInputModifiers.Meta, PhysicalKey.MetaLeft, null);

        shown.Window.KeyPress(Key.R, RawInputModifiers.Meta, PhysicalKey.R, "r");
        shown.Window.KeyRelease(Key.R, RawInputModifiers.Meta, PhysicalKey.R, "r");

        shown.Channel.Sent.Should().Equal("press Super_L", "press key R 'r'", "release key R 'r'");
        shown.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LosingTheKeyboard_ReleasesTheKeyBeforeItsModifier(bool deactivateTheWindow)
    {
        var shown = Show(KeyboardHost.Windows);
        shown.Window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        shown.Window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);

        if (deactivateTheWindow) FramebufferSurfaceInputTests.Deactivate(shown.Window);
        else shown.Other.Focus();

        shown.Channel.Sent.Should().Equal(
            "press Control_L", "press key A 'a'", "release key A 'a'", "release Control_L");
        shown.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(Key.K, PhysicalKey.K)]
    [InlineData(Key.P, PhysicalKey.P)]
    public void PolyTermsOwnShortcuts_AreLeftForTheWindow(Key key, PhysicalKey physicalKey)
    {
        var shown = Show(KeyboardHost.Windows);

        shown.Window.KeyPress(key, RawInputModifiers.Control | RawInputModifiers.Shift, physicalKey, null);

        shown.Channel.Sent.Should().BeEmpty();
        shown.Handled.Should().Equal(false);
        shown.Window.Close();
    }

    [Fact]
    public void EveryKeyTheViewCanSendAsAKey_HasItsOwnRdpScancode()
    {
        var scancodes = CharacterKeys.All.Select(key =>
        {
            RdpScancodes.TryMapPhysical(key.ToString(), out var scancode).Should().BeTrue($"{key} needs a scancode");
            return scancode;
        }).ToList();

        scancodes.Should().OnlyHaveUniqueItems();
    }

    private sealed record Shown(Window Window, Button Other, FramebufferSurfaceInputTests.RecordingChannel Channel,
        List<bool> Handled);

    /// <summary><c>Handled</c> records, for each key-down, whether it was marked handled.</summary>
    private static Shown Show(KeyboardHost host)
    {
        var (window, surface, channel, other) = FramebufferSurfaceInputTests.Show();
        surface.KeyboardHost = host;
        var handled = new List<bool>();
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => handled.Add(e.Handled),
            RoutingStrategies.Bubble, handledEventsToo: true);
        return new Shown(window, other, channel, handled);
    }
}
