using Avalonia.Input;

namespace PolyTerm.App.Surfaces.Framebuffer;

/// <summary>Which operating system's keyboard rules apply to this computer.</summary>
internal enum KeyboardHost { Windows, MacOS, Linux }

/// <summary>
/// The keys that normally reach the remote as typed text - letters, digits, punctuation, Space and the
/// number pad - and when one has to be sent as a key instead (issue #398). Typed text only carries the
/// character, so a shortcut such as Ctrl+C has to go as the key itself for the remote to see it. Has no
/// dependency on a live control, so it can be tested directly.
/// </summary>
internal static class CharacterKeys
{
    internal static readonly IReadOnlySet<PhysicalKey> All = new HashSet<PhysicalKey>
    {
        PhysicalKey.A, PhysicalKey.B, PhysicalKey.C, PhysicalKey.D, PhysicalKey.E, PhysicalKey.F, PhysicalKey.G,
        PhysicalKey.H, PhysicalKey.I, PhysicalKey.J, PhysicalKey.K, PhysicalKey.L, PhysicalKey.M, PhysicalKey.N,
        PhysicalKey.O, PhysicalKey.P, PhysicalKey.Q, PhysicalKey.R, PhysicalKey.S, PhysicalKey.T, PhysicalKey.U,
        PhysicalKey.V, PhysicalKey.W, PhysicalKey.X, PhysicalKey.Y, PhysicalKey.Z,
        PhysicalKey.Digit0, PhysicalKey.Digit1, PhysicalKey.Digit2, PhysicalKey.Digit3, PhysicalKey.Digit4,
        PhysicalKey.Digit5, PhysicalKey.Digit6, PhysicalKey.Digit7, PhysicalKey.Digit8, PhysicalKey.Digit9,
        PhysicalKey.Backquote, PhysicalKey.Minus, PhysicalKey.Equal, PhysicalKey.BracketLeft,
        PhysicalKey.BracketRight, PhysicalKey.Backslash, PhysicalKey.Semicolon, PhysicalKey.Quote,
        PhysicalKey.Comma, PhysicalKey.Period, PhysicalKey.Slash,
        PhysicalKey.IntlBackslash, PhysicalKey.IntlRo, PhysicalKey.IntlYen,
        PhysicalKey.Space,
        PhysicalKey.NumPad0, PhysicalKey.NumPad1, PhysicalKey.NumPad2, PhysicalKey.NumPad3, PhysicalKey.NumPad4,
        PhysicalKey.NumPad5, PhysicalKey.NumPad6, PhysicalKey.NumPad7, PhysicalKey.NumPad8, PhysicalKey.NumPad9,
        PhysicalKey.NumPadDecimal, PhysicalKey.NumPadAdd, PhysicalKey.NumPadSubtract,
        PhysicalKey.NumPadMultiply, PhysicalKey.NumPadDivide,
    };

    internal static KeyboardHost CurrentHost { get; } =
        OperatingSystem.IsWindows() ? KeyboardHost.Windows
        : OperatingSystem.IsMacOS() ? KeyboardHost.MacOS
        : KeyboardHost.Linux;

    /// <summary>
    /// Whether a press of this key has to be sent as a key. If not, the text it types is sent instead.
    /// <paramref name="keySymbol"/> is <see cref="KeyEventArgs.KeySymbol"/>.
    /// </summary>
    internal static bool SendsAsKey(PhysicalKey key, KeyModifiers modifiers, string? keySymbol, KeyboardHost host)
    {
        if (!All.Contains(key)) return false;
        if (modifiers.HasFlag(KeyModifiers.Meta)) return true;

        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        // Windows reports AltGr as Ctrl+Alt. A key it types a character with has a symbol; one that's
        // only a shortcut doesn't.
        if (modifiers.HasFlag(KeyModifiers.Control))
            return !(host == KeyboardHost.Windows && alt && keySymbol is not null);

        // Option on macOS types characters.
        if (!alt || host == KeyboardHost.MacOS) return false;

        // On Windows, digits entered on the number pad with Alt held type a character when Alt comes up.
        return !(host == KeyboardHost.Windows && key is >= PhysicalKey.NumPad0 and <= PhysicalKey.NumPad9);
    }

    /// <summary>
    /// The character on a key, for protocols that identify a key by what it types (VNC). Null if there's
    /// no telling. Letters are cased by Shift alone, and are Latin even on a layout that types another
    /// script, as that's what shortcuts are made of.
    /// </summary>
    internal static char? CharacterFor(Key key, PhysicalKey physicalKey, string? keySymbol, bool shift)
    {
        if (key is >= Key.A and <= Key.Z)
            return (char)((shift ? 'A' : 'a') + (key - Key.A));
        if (keySymbol is [var typed] && !char.IsControl(typed) && !char.IsSurrogate(typed))
            return typed;
        // Windows gives no symbol while Ctrl is held, so go by what the key is on a US keyboard.
        return physicalKey.ToQwertyKeySymbol(shift) is [var qwerty] ? qwerty : null;
    }
}
