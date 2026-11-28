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
    private readonly List<(PhysicalKey Key, string Symbol)> _characters = [];

    public void Press(Key key)
    {
        if (!_keys.Contains(key)) _keys.Add(key); // auto-repeat sends the same press again
    }

    public void Release(Key key) => _keys.Remove(key);

    // Modifiers that are down here and that the remote hasn't been told about (macOS's Option).
    private readonly List<Key> _heldBack = [];
    private bool _heldBackWasUsed;

    /// <summary>
    /// Notes a modifier that's down but isn't to be sent until something needs it (issue #398). While
    /// the remote holds Alt it drops typed text, and on macOS Option is how many characters are typed.
    /// </summary>
    public void HoldBack(Key key)
    {
        if (_keys.Contains(key) || _heldBack.Contains(key)) return;
        if (_heldBack.Count == 0) _heldBackWasUsed = false;
        _heldBack.Add(key);
    }

    /// <summary>Notes that something else happened while the held-back keys were down, so letting them go isn't a tap.</summary>
    public void UseHeldBack() => _heldBackWasUsed = true;

    /// <summary>Returns the held-back keys, oldest first, for the caller to press now; they count as held from here.</summary>
    public IReadOnlyList<Key> TakeHeldBackToPress()
    {
        var keys = _heldBack.ToArray();
        _heldBack.Clear();
        foreach (var key in keys) Press(key);
        return keys;
    }

    /// <summary>
    /// Forgets a held-back key. Returns whether it was tapped on its own, so the caller still has to send
    /// it, or null if it wasn't being held back.
    /// </summary>
    public bool? ReleaseHeldBack(Key key) => _heldBack.Remove(key) ? !_heldBackWasUsed : null;

    /// <summary>
    /// Goes back to holding back any <paramref name="modifier"/> keys that have been pressed, and returns
    /// them, newest first, for the caller to release.
    /// </summary>
    public IReadOnlyList<Key> HoldBackAgain(KeyModifiers modifier)
    {
        var keys = Take(_keys, key => ModifierFor(key) == modifier);
        _heldBack.AddRange(keys.Reverse());
        UseHeldBack();
        return keys;
    }

    public void Press(PointerButton button)
    {
        if (!_buttons.Contains(button)) _buttons.Add(button);
    }

    public void Release(PointerButton button) => _buttons.Remove(button);

    public bool HasButtons => _buttons.Count > 0;

    /// <summary>
    /// Holds a character key that was sent as a key (issue #398), and returns the symbol to send for
    /// this press. A key that's already down keeps the symbol it went down with, so that its release
    /// matches even if Shift changed in between.
    /// </summary>
    public string PressCharacter(PhysicalKey key, string symbol)
    {
        var index = _characters.FindIndex(held => held.Key == key);
        if (index >= 0) return _characters[index].Symbol;
        _characters.Add((key, symbol));
        return symbol;
    }

    /// <summary>Forgets a held character key and returns the symbol to release it with, or null if it isn't held.</summary>
    public string? ReleaseCharacter(PhysicalKey key)
    {
        var index = _characters.FindIndex(held => held.Key == key);
        if (index < 0) return null;
        var symbol = _characters[index].Symbol;
        _characters.RemoveAt(index);
        return symbol;
    }

    /// <summary>Forgets every held character key and returns them, newest first, for the caller to release.</summary>
    public IReadOnlyList<(PhysicalKey Key, string Symbol)> TakeCharacters() => Take(_characters, _ => true);

    /// <summary>
    /// Forgets every held key and returns them, newest first, for the caller to release. Held-back keys
    /// are forgotten too, but not returned, as the remote never had them.
    /// </summary>
    public IReadOnlyList<Key> TakeKeys()
    {
        _heldBack.Clear();
        return Take(_keys, _ => true);
    }

    /// <summary>Forgets every held mouse button and returns them, newest first, for the caller to release.</summary>
    public IReadOnlyList<PointerButton> TakeButtons() => Take(_buttons, _ => true);

    /// <summary>
    /// Forgets and returns the held modifier keys that an input event shows are no longer down. Only
    /// modifiers in <paramref name="reliable"/> are judged this way (see <see cref="ReliableModifiers"/>).
    /// Both keys of a pair share one flag, so neither is returned while either is still down.
    /// </summary>
    public IReadOnlyList<Key> TakeModifiersNotDown(KeyModifiers reported, KeyModifiers reliable)
    {
        bool NotDown(Key key)
        {
            var modifier = ModifierFor(key) & reliable;
            return modifier != KeyModifiers.None && (reported & modifier) == KeyModifiers.None;
        }

        _heldBack.RemoveAll(NotDown); // the remote never had these, so there's nothing to release
        return Take(_keys, NotDown);
    }

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
