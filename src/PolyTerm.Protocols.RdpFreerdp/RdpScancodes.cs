namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// Named-control-key (the exact strings <c>FramebufferSurface.ControlKeyMap</c> produces for
/// non-printable keys - Enter, arrows, F-keys, modifiers, etc.) to RDP scancode (FreeRDP's own
/// composite <c>RDP_SCANCODE_*</c> encoding: low byte the raw PC/AT set-1 byte, bit 0x100 set for
/// "extended" keys - see <c>freerdp/scancode.h</c>'s own <c>MAKE_RDP_SCANCODE</c>/<c>KBDEXT</c>)
/// mapping. Typed characters do NOT go through here - <see cref="RdpFreerdpFramebufferChannel.SendKeyAsync"/>
/// routes the numeric X11-keysym codepoint strings <c>FramebufferSurface.OnTextInput</c> sends
/// straight to the shim's Unicode keyboard-input entry point instead, sidestepping scancodes (and
/// therefore keyboard-layout/shift-state) entirely for typed text - the earlier version of this map
/// held literal single-character entries ("a", "1", ...) for that case, but nothing ever actually
/// produced a <c>KeySymbol</c> in that shape (only the numeric codepoint form), so every printable
/// keystroke was silently dropped - confirmed against a real connection, not just reasoned about.
/// </summary>
internal static class RdpScancodes
{
    private const uint Ext = 0x0100;

    private static readonly Dictionary<string, uint> Map = new(StringComparer.Ordinal)
    {
        ["space"] = 0x39,
        ["Return"] = 0x1C,
        ["BackSpace"] = 0x0E,
        ["Tab"] = 0x0F,
        ["Escape"] = 0x01,

        ["Up"] = 0x48 | Ext,
        ["Down"] = 0x50 | Ext,
        ["Left"] = 0x4B | Ext,
        ["Right"] = 0x4D | Ext,
        ["Home"] = 0x47 | Ext,
        ["End"] = 0x4F | Ext,
        ["Prior"] = 0x49 | Ext, // Page Up
        ["Next"] = 0x51 | Ext, // Page Down
        ["Insert"] = 0x52 | Ext,
        ["Delete"] = 0x53 | Ext,

        ["Shift_L"] = 0x2A,
        ["Shift_R"] = 0x36,
        ["Control_L"] = 0x1D,
        ["Control_R"] = 0x1D | Ext,
        ["Alt_L"] = 0x38,
        ["Alt_R"] = 0x38 | Ext,
        ["Super_L"] = 0x5B | Ext,
        ["Super_R"] = 0x5C | Ext,
        ["Caps_Lock"] = 0x3A,
        ["Num_Lock"] = 0x45,
        ["Scroll_Lock"] = 0x46,

        ["F1"] = 0x3B, ["F2"] = 0x3C, ["F3"] = 0x3D, ["F4"] = 0x3E,
        ["F5"] = 0x3F, ["F6"] = 0x40, ["F7"] = 0x41, ["F8"] = 0x42,
        ["F9"] = 0x43, ["F10"] = 0x44, ["F11"] = 0x57, ["F12"] = 0x58,

        ["Print"] = 0x37 | Ext,
        ["Pause"] = 0x46 | Ext,
    };

    // Keys that normally type a character, by KeyEvent.PhysicalKey, for when one is sent as a key.
    private static readonly Dictionary<string, uint> PhysicalMap = new(StringComparer.Ordinal)
    {
        ["Digit1"] = 0x02, ["Digit2"] = 0x03, ["Digit3"] = 0x04, ["Digit4"] = 0x05, ["Digit5"] = 0x06,
        ["Digit6"] = 0x07, ["Digit7"] = 0x08, ["Digit8"] = 0x09, ["Digit9"] = 0x0A, ["Digit0"] = 0x0B,
        ["Minus"] = 0x0C, ["Equal"] = 0x0D,

        ["Q"] = 0x10, ["W"] = 0x11, ["E"] = 0x12, ["R"] = 0x13, ["T"] = 0x14,
        ["Y"] = 0x15, ["U"] = 0x16, ["I"] = 0x17, ["O"] = 0x18, ["P"] = 0x19,
        ["BracketLeft"] = 0x1A, ["BracketRight"] = 0x1B,

        ["A"] = 0x1E, ["S"] = 0x1F, ["D"] = 0x20, ["F"] = 0x21, ["G"] = 0x22,
        ["H"] = 0x23, ["J"] = 0x24, ["K"] = 0x25, ["L"] = 0x26,
        ["Semicolon"] = 0x27, ["Quote"] = 0x28, ["Backquote"] = 0x29, ["Backslash"] = 0x2B,

        ["Z"] = 0x2C, ["X"] = 0x2D, ["C"] = 0x2E, ["V"] = 0x2F, ["B"] = 0x30,
        ["N"] = 0x31, ["M"] = 0x32,
        ["Comma"] = 0x33, ["Period"] = 0x34, ["Slash"] = 0x35,

        ["Space"] = 0x39,
        ["IntlBackslash"] = 0x56, ["IntlRo"] = 0x73, ["IntlYen"] = 0x7D,

        ["NumPad7"] = 0x47, ["NumPad8"] = 0x48, ["NumPad9"] = 0x49, ["NumPadSubtract"] = 0x4A,
        ["NumPad4"] = 0x4B, ["NumPad5"] = 0x4C, ["NumPad6"] = 0x4D, ["NumPadAdd"] = 0x4E,
        ["NumPad1"] = 0x4F, ["NumPad2"] = 0x50, ["NumPad3"] = 0x51, ["NumPad0"] = 0x52,
        ["NumPadDecimal"] = 0x53, ["NumPadMultiply"] = 0x37, ["NumPadDivide"] = 0x35 | Ext,
    };

    public static bool TryMap(string keySymbol, out uint scancode) => Map.TryGetValue(keySymbol, out scancode);

    /// <summary>The scancode for a <c>KeyEvent.PhysicalKey</c>.</summary>
    public static bool TryMapPhysical(string physicalKey, out uint scancode) => PhysicalMap.TryGetValue(physicalKey, out scancode);
}
