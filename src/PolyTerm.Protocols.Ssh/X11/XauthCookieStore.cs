using System.Globalization;
using System.Text;

namespace PolyTerm.Protocols.Ssh.X11;

/// <summary>
/// Reads the real local X11 authentication cookie from the standard Xauthority file (the
/// <c>XAUTHORITY</c> env var if set, else <c>~/.Xauthority</c> — the convention every real X client/
/// server follows), so <see cref="X11Forwarder"/> can rewrite the disposable fake cookie sent over the
/// wire in the x11-req back to the real one via <see cref="X11ConnectionSetupRewriter"/>.
/// </summary>
internal static class XauthCookieStore
{
    private const string MitMagicCookieProtocol = "MIT-MAGIC-COOKIE-1";

    /// <summary>
    /// FamilyWild (all 1-bits) — an Xauthority entry using this family matches any display, which
    /// xauth itself creates for some setups (e.g. after <c>xauth generate</c> with no explicit host).
    /// </summary>
    private const ushort FamilyWild = 0xFFFF;

    /// <summary>
    /// The MIT-MAGIC-COOKIE-1 entry for <paramref name="displayNumber"/> from the local Xauthority
    /// file, or null if the file doesn't exist or has no matching entry — a legitimate, expected case
    /// (many Windows X servers don't use xauth at all, e.g. VcXsrv run with "Disable access control"),
    /// not an error.
    /// </summary>
    public static byte[]? TryReadCookie(int displayNumber)
    {
        var path = ResolvePath();
        if (path is null || !File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            return ReadCookie(stream, displayNumber);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the Xauthority binary format: a flat sequence of records, each a 2-byte big-endian
    /// family followed by four 2-byte-length-prefixed byte fields (address, display number — as an
    /// ASCII decimal string, not a binary integer — authorization-protocol-name, and authorization-
    /// protocol-data). Returns the first MIT-MAGIC-COOKIE-1 entry whose display number matches, or
    /// whose family is <see cref="FamilyWild"/>. Internal (not private) so tests can feed it a crafted
    /// in-memory stream without needing a real file on disk.
    /// </summary>
    internal static byte[]? ReadCookie(Stream stream, int displayNumber)
    {
        var displayText = displayNumber.ToString(CultureInfo.InvariantCulture);
        while (TryReadRecord(stream, out var family, out var numberBytes, out var nameBytes, out var dataBytes))
        {
            if (Encoding.ASCII.GetString(nameBytes) != MitMagicCookieProtocol) continue;
            if (family == FamilyWild || Encoding.ASCII.GetString(numberBytes) == displayText)
                return dataBytes;
        }
        return null;
    }

    private static bool TryReadRecord(Stream stream, out ushort family, out byte[] number, out byte[] name, out byte[] data)
    {
        family = 0; number = []; name = []; data = [];
        if (!TryReadUInt16(stream, out family)) return false;
        if (!TryReadLengthPrefixed(stream, out _)) return false; // address — not needed for matching
        if (!TryReadLengthPrefixed(stream, out number)) return false;
        if (!TryReadLengthPrefixed(stream, out name)) return false;
        if (!TryReadLengthPrefixed(stream, out data)) return false;
        return true;
    }

    private static bool TryReadLengthPrefixed(Stream stream, out byte[] data)
    {
        data = [];
        if (!TryReadUInt16(stream, out var length)) return false;
        data = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = stream.Read(data, read, length - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static bool TryReadUInt16(Stream stream, out ushort value)
    {
        value = 0;
        var buf = new byte[2];
        var read = 0;
        while (read < 2)
        {
            var n = stream.Read(buf, read, 2 - read);
            if (n == 0) return false;
            read += n;
        }
        value = (ushort)((buf[0] << 8) | buf[1]);
        return true;
    }

    private static string? ResolvePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("XAUTHORITY");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".Xauthority");
    }
}
