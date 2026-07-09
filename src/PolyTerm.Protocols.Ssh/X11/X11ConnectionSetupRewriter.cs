using System.Text;

namespace PolyTerm.Protocols.Ssh.X11;

/// <summary>
/// Rewrites the authorization-protocol-data field of an X11 client's initial connection-setup packet
/// (the "ConnClientPrefix" — the very first bytes any X11 client sends on a fresh connection, before
/// anything else) from the disposable fake cookie <see cref="SshSession"/> sent to the remote host in
/// its x11-req back to the real local xauth cookie, so the real local X server (which only recognizes
/// its own real cookie) accepts the connection. This is exactly what real <c>ssh -X</c> does, and the
/// whole reason a fake cookie is generated at all: the real cookie never has to leave this machine.
/// Only the first packet of each connection needs inspecting — this fixed-format prefix is always a
/// single client write() before any other X protocol traffic.
/// </summary>
internal static class X11ConnectionSetupRewriter
{
    private const string MitMagicCookieProtocol = "MIT-MAGIC-COOKIE-1";
    private const int FixedHeaderLength = 12;

    /// <summary>
    /// Returns <paramref name="data"/> unchanged unless it's recognizably an X11 ConnClientPrefix
    /// whose authorization-protocol-data exactly matches <paramref name="fakeCookie"/> — in which case
    /// a copy with that field replaced by <paramref name="realCookie"/> is returned instead. Never
    /// throws on malformed/short/fragmented input; anything that doesn't cleanly parse is left alone
    /// and forwarded as-is (better a rejected/garbled connection the user can investigate than PolyTerm
    /// silently corrupting arbitrary X11 traffic it misidentified).
    /// </summary>
    public static byte[] TryRewriteCookie(byte[] data, byte[] fakeCookie, byte[]? realCookie)
    {
        if (realCookie is null || data.Length < FixedHeaderLength) return data;

        // Byte 0 declares the byte order for every 2-byte field that follows: 'B' (0x42) = most-
        // significant byte first, 'l' (0x6C) = least-significant byte first. Neither value present
        // means this isn't a ConnClientPrefix at all (or we're looking at a fragment past it).
        var bigEndian = data[0] switch { 0x42 => true, 0x6C => false, _ => (bool?)null };
        if (bigEndian is null) return data;

        int ReadU16(int offset) => bigEndian.Value
            ? (data[offset] << 8) | data[offset + 1]
            : (data[offset + 1] << 8) | data[offset];

        var nameLen = ReadU16(6);
        var dataLen = ReadU16(8);
        var nameOffset = FixedHeaderLength;
        var dataOffset = nameOffset + nameLen + Pad(nameLen);
        var dataEnd = dataOffset + dataLen;
        if (nameLen < 0 || dataLen < 0 || dataEnd > data.Length) return data;

        var protocolName = Encoding.ASCII.GetString(data, nameOffset, nameLen);
        if (protocolName != MitMagicCookieProtocol || dataLen != fakeCookie.Length) return data;
        if (!data.AsSpan(dataOffset, dataLen).SequenceEqual(fakeCookie)) return data;

        var rewritten = (byte[])data.Clone();
        realCookie.AsSpan(0, Math.Min(realCookie.Length, dataLen)).CopyTo(rewritten.AsSpan(dataOffset));
        return rewritten;
    }

    private static int Pad(int n) => (4 - (n % 4)) % 4;
}
