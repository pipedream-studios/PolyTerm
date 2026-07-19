namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// Best-effort X11-keysym-name (the string shape <see cref="PolyTerm.Engine.KeyEvent.KeySymbol"/> uses,
/// matching <c>VncFramebufferChannel</c>'s own convention) to RDP scancode (PC/AT set-1, per FreeRDP's
/// own <c>freerdp/scancode.h</c>) mapping. Deliberately not exhaustive — covers ASCII letters/digits and
/// the handful of control keys needed to prove input injection works at all (first feature-ladder rung:
/// connect/display/input). Unmapped symbols are silently ignored by <see cref="RdpFreerdpFramebufferChannel.SendKeyAsync"/>.
/// </summary>
internal static class RdpScancodes
{
    private static readonly Dictionary<string, uint> Map = BuildMap();

    public static bool TryMap(string keySymbol, out uint scancode) => Map.TryGetValue(keySymbol, out scancode);

    private static Dictionary<string, uint> BuildMap()
    {
        var map = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["space"] = 0x39,
            ["Return"] = 0x1C,
            ["BackSpace"] = 0x0E,
            ["Tab"] = 0x0F,
            ["Escape"] = 0x01,
        };

        // Row 1 of a US QWERTY keyboard - scancodes 0x02-0x0D, digits 1-9 then 0.
        const string digitRow = "1234567890";
        for (var i = 0; i < digitRow.Length; i++)
            map[digitRow[i].ToString()] = (uint)(0x02 + i);

        // Scancodes for a-z in physical QWERTY key order (not alphabetical) - matches FreeRDP's own
        // RDP_SCANCODE_KEY_* table for the US layout.
        const string qwertyRow1 = "qwertyuiop";
        for (var i = 0; i < qwertyRow1.Length; i++)
            map[qwertyRow1[i].ToString()] = (uint)(0x10 + i);

        const string qwertyRow2 = "asdfghjkl";
        for (var i = 0; i < qwertyRow2.Length; i++)
            map[qwertyRow2[i].ToString()] = (uint)(0x1E + i);

        const string qwertyRow3 = "zxcvbnm";
        for (var i = 0; i < qwertyRow3.Length; i++)
            map[qwertyRow3[i].ToString()] = (uint)(0x2C + i);

        // Uppercase letters share the same scancode as their lowercase counterpart - RDP conveys
        // shift state via a separate Shift key-down event, not a different scancode per case.
        foreach (var (lower, code) in map.Where(kv => kv.Key.Length == 1 && char.IsLower(kv.Key[0])).ToList())
            map[lower.ToUpperInvariant()] = code;

        return map;
    }
}
