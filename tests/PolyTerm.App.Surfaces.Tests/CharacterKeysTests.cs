using Avalonia.Input;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Framebuffer;
using Xunit;

namespace PolyTerm.App.Surfaces.Tests;

/// <summary>
/// Covers when a character key has to be sent as a key (issue #398); <c>FramebufferSurfaceShortcutTests</c>
/// in PolyTerm.App.Tests covers what the surface then sends. Hosts are passed by name, as the enum is
/// internal and test methods are public.
/// </summary>
public sealed class CharacterKeysTests
{
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Alt = KeyModifiers.Alt;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Meta = KeyModifiers.Meta;

    private const string Windows = nameof(KeyboardHost.Windows);
    private const string MacOS = nameof(KeyboardHost.MacOS);
    private const string Linux = nameof(KeyboardHost.Linux);

    private static bool SendsAsKey(string host, PhysicalKey key, KeyModifiers modifiers, string? symbol) =>
        CharacterKeys.SendsAsKey(key, modifiers, symbol, Enum.Parse<KeyboardHost>(host));

    [Theory]
    [InlineData(KeyModifiers.None, null, false)]
    [InlineData(Shift, "C", false)]
    [InlineData(Ctrl, null, true)]
    [InlineData(Ctrl, "c", true)]
    [InlineData(Ctrl | Shift, "C", true)]
    [InlineData(Meta, "c", true)]
    [InlineData(Meta | Shift, "C", true)]
    [InlineData(Meta | Alt, "ç", true)]
    [InlineData(Ctrl | Alt, null, true)]
    public void SendsAsKey_ForTheSameModifiersOnEveryHost(KeyModifiers modifiers, string? symbol, bool expected)
    {
        foreach (var host in Enum.GetNames<KeyboardHost>())
            SendsAsKey(host, PhysicalKey.C, modifiers, symbol).Should().Be(expected, $"on {host}");
    }

    [Theory]
    // AltGr is Ctrl+Alt on Windows; a symbol means it types a character.
    [InlineData(Windows, Ctrl | Alt, "@", false)]
    [InlineData(Windows, Ctrl | Alt | Shift, "Ω", false)]
    // Elsewhere AltGr isn't reported as Ctrl or Alt, so Ctrl+Alt is a shortcut.
    [InlineData(Linux, Ctrl | Alt, "t", true)]
    [InlineData(MacOS, Ctrl | Alt, "†", true)]
    // Alt on its own is a shortcut, except on macOS, where Option types characters.
    [InlineData(Windows, Alt, "f", true)]
    [InlineData(Linux, Alt, "f", true)]
    [InlineData(MacOS, Alt, "ƒ", false)]
    [InlineData(MacOS, Alt | Shift, "Ï", false)]
    public void SendsAsKey_FollowsTheHostsUseOfAlt(string host, KeyModifiers modifiers, string symbol, bool expected) =>
        SendsAsKey(host, PhysicalKey.F, modifiers, symbol).Should().Be(expected);

    [Theory]
    [InlineData(Windows, Alt, false)] // typing a character by its number
    [InlineData(Windows, Ctrl, true)]
    [InlineData(Linux, Alt, true)]
    public void SendsAsKey_LeavesAltCodesToWindows(string host, KeyModifiers modifiers, bool expected) =>
        SendsAsKey(host, PhysicalKey.NumPad1, modifiers, null).Should().Be(expected);

    [Theory]
    [InlineData(PhysicalKey.Enter)]
    [InlineData(PhysicalKey.F4)]
    [InlineData(PhysicalKey.ControlLeft)]
    [InlineData(PhysicalKey.None)]
    public void SendsAsKey_LeavesOtherKeysAlone(PhysicalKey key) =>
        SendsAsKey(Windows, key, Ctrl, null).Should().BeFalse();

    [Theory]
    // A letter goes by the layout's key and Shift, whatever the platform reports it typing.
    [InlineData(Key.C, PhysicalKey.C, null, false, 'c')]
    [InlineData(Key.C, PhysicalKey.C, null, true, 'C')]
    [InlineData(Key.A, PhysicalKey.Q, "a", false, 'a')]   // French layout
    [InlineData(Key.C, PhysicalKey.C, "с", false, 'c')]   // Cyrillic layout
    // Anything else goes by its symbol, or by the US keyboard when there's none.
    [InlineData(Key.D1, PhysicalKey.Digit1, "!", true, '!')]
    [InlineData(Key.D1, PhysicalKey.Digit1, null, true, '!')]
    [InlineData(Key.OemMinus, PhysicalKey.Minus, null, false, '-')]
    [InlineData(Key.Space, PhysicalKey.Space, null, false, ' ')]
    [InlineData(Key.NumPad5, PhysicalKey.NumPad5, null, false, '5')]
    public void CharacterFor_NamesTheCharacterOnTheKey(Key key, PhysicalKey physicalKey, string? symbol, bool shift, char expected) =>
        CharacterKeys.CharacterFor(key, physicalKey, symbol, shift).Should().Be(expected);

    [Fact]
    public void CharacterFor_ReturnsNull_WhenThereIsNoTelling() =>
        CharacterKeys.CharacterFor(Key.None, PhysicalKey.IntlRo, null, shift: false).Should().BeNull();
}
