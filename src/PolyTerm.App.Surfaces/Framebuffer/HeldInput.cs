using Avalonia.Input;
using PolyTerm.Engine;

namespace PolyTerm.App.Surfaces.Framebuffer;

/// <summary>
/// The keys and mouse buttons <see cref="FramebufferSurface"/> has sent to the remote as down and not
/// yet as up (issue #368). A release only reaches the surface if it still has the keyboard when the key
/// comes up, so it has to know what to let go of when that isn't going to happen. Has no dependency on
/// a live control or channel, so it can be tested directly.
/// </summary>
internal sealed class HeldInput
{
    // Lists rather than sets: releases go out newest first, and there are only ever a few entries.
    private readonly List<Key> _keys = [];
    private readonly List<PointerButton> _buttons = [];

    public void Press(Key key)
    {
        if (!_keys.Contains(key)) _keys.Add(key); // auto-repeat sends the same press again
    }

    public void Release(Key key) => _keys.Remove(key);

    public void Press(PointerButton button)
    {
        if (!_buttons.Contains(button)) _buttons.Add(button);
    }

    public void Release(PointerButton button) => _buttons.Remove(button);

    /// <summary>Forgets every held key and returns them, newest first, for the caller to release.</summary>
    public IReadOnlyList<Key> TakeKeys() => Take(_keys, _ => true);

    /// <summary>Forgets every held mouse button and returns them, newest first, for the caller to release.</summary>
    public IReadOnlyList<PointerButton> TakeButtons() => Take(_buttons, _ => true);

    /// <summary>
    /// Forgets and returns the held modifier keys that an input event shows are no longer down. Only
    /// modifiers in <paramref name="reliable"/> are judged this way (see <see cref="ReliableModifiers"/>).
    /// Both keys of a pair share one flag, so neither is returned while either is still down.
    /// </summary>
    public IReadOnlyList<Key> TakeModifiersNotDown(KeyModifiers reported, KeyModifiers reliable) =>
        Take(_keys, key =>
        {
            var modifier = ModifierFor(key) & reliable;
            return modifier != KeyModifiers.None && (reported & modifier) == KeyModifiers.None;
        });

    private static IReadOnlyList<T> Take<T>(List<T> held, Func<T, bool> match)
    {
        var taken = held.Where(match).Reverse().ToArray();
        foreach (var item in taken) held.Remove(item);
        return taken;
    }

    /// <summary>The modifier flag <paramref name="key"/> sets, or <see cref="KeyModifiers.None"/> if it isn't a modifier key.</summary>
    internal static KeyModifiers ModifierFor(Key key) => key switch
    {
        Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
        Key.LeftCtrl or Key.RightCtrl   => KeyModifiers.Control,
        Key.LeftAlt or Key.RightAlt     => KeyModifiers.Alt,
        Key.LWin or Key.RWin            => KeyModifiers.Meta,
        _ => KeyModifiers.None,
    };

    /// <summary>
    /// The modifiers whose absence from an input event proves the key is up. Alt only counts on macOS:
    /// on Linux, Avalonia reports it from X11's Mod1 alone, which AltGr doesn't normally set, so a held
    /// AltGr would look released.
    /// </summary>
    internal static KeyModifiers ReliableModifiers(bool isMacOS)
    {
        var modifiers = KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Meta;
        return isMacOS ? modifiers | KeyModifiers.Alt : modifiers;
    }
}
