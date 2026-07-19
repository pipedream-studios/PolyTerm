namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// Named-control-key (the exact strings <c>FramebufferSurface.ControlKeyMap</c> produces for
/// non-printable keys - Enter, arrows, F-keys, modifiers, etc.) to RDP scancode (FreeRDP's own
/// composite <c>RDP_SCANCODE_*</c> encoding: low byte the raw PC/AT set-1 byte, bit 0x100 set for
/// "extended" keys - see <c>freerdp/scancode.h</c>'s own <c>MAKE_RDP_SCANCODE</c>/<c>KBDEXT</c>)
/// mapping. Printable characters do NOT go through here - <see cref="RdpFreerdpFramebufferChannel.SendKeyAsync"/>
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

    public static bool TryMap(string keySymbol, out uint scancode) => Map.TryGetValue(keySymbol, out scancode);
}
