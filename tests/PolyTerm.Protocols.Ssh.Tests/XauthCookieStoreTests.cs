using FluentAssertions;
using PolyTerm.Protocols.Ssh.X11;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>Pure Xauthority binary-format parsing — a crafted in-memory stream, no real ~/.Xauthority file or X server involved.</summary>
public sealed class XauthCookieStoreTests
{
    private const ushort FamilyLocal = 256;
    private const ushort FamilyWild = 0xFFFF;

    private static void WriteU16(MemoryStream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteLengthPrefixed(MemoryStream stream, byte[] data)
    {
        WriteU16(stream, data.Length);
        stream.Write(data);
    }

    private static void WriteRecord(MemoryStream stream, ushort family, string address, string displayNumber, string protocolName, byte[] data)
    {
        WriteU16(stream, family);
        WriteLengthPrefixed(stream, System.Text.Encoding.ASCII.GetBytes(address));
        WriteLengthPrefixed(stream, System.Text.Encoding.ASCII.GetBytes(displayNumber));
        WriteLengthPrefixed(stream, System.Text.Encoding.ASCII.GetBytes(protocolName));
        WriteLengthPrefixed(stream, data);
    }

    [Fact]
    public void FindsTheCookie_ForAMatchingDisplayNumber()
    {
        var cookie = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var stream = new MemoryStream();
        WriteRecord(stream, FamilyLocal, "myhost", "0", "MIT-MAGIC-COOKIE-1", cookie);
        stream.Position = 0;

        var result = XauthCookieStore.ReadCookie(stream, displayNumber: 0);

        result.Should().Equal(cookie);
    }

    [Fact]
    public void SkipsEntries_ForADifferentDisplayNumber()
    {
        var wrongDisplayCookie = new byte[16];
        var rightDisplayCookie = Enumerable.Repeat((byte)0xAB, 16).ToArray();
        using var stream = new MemoryStream();
        WriteRecord(stream, FamilyLocal, "myhost", "1", "MIT-MAGIC-COOKIE-1", wrongDisplayCookie);
        WriteRecord(stream, FamilyLocal, "myhost", "0", "MIT-MAGIC-COOKIE-1", rightDisplayCookie);
        stream.Position = 0;

        var result = XauthCookieStore.ReadCookie(stream, displayNumber: 0);

        result.Should().Equal(rightDisplayCookie);
    }

    [Fact]
    public void FamilyWildEntry_MatchesAnyDisplayNumber()
    {
        var cookie = Enumerable.Repeat((byte)0xCD, 16).ToArray();
        using var stream = new MemoryStream();
        WriteRecord(stream, FamilyWild, "", "999", "MIT-MAGIC-COOKIE-1", cookie);
        stream.Position = 0;

        var result = XauthCookieStore.ReadCookie(stream, displayNumber: 42);

        result.Should().Equal(cookie, "FamilyWild entries apply regardless of the recorded display number");
    }

    [Fact]
    public void SkipsEntries_WithADifferentAuthProtocol()
    {
        using var stream = new MemoryStream();
        WriteRecord(stream, FamilyLocal, "myhost", "0", "XDM-AUTHORIZATION-1", new byte[16]);
        stream.Position = 0;

        var result = XauthCookieStore.ReadCookie(stream, displayNumber: 0);

        result.Should().BeNull("this code only understands MIT-MAGIC-COOKIE-1, matching X11ConnectionSetupRewriter");
    }

    [Fact]
    public void EmptyFile_ReturnsNull()
    {
        using var stream = new MemoryStream();
        XauthCookieStore.ReadCookie(stream, displayNumber: 0).Should().BeNull();
    }

    [Fact]
    public void NoMatchingEntry_ReturnsNull()
    {
        using var stream = new MemoryStream();
        WriteRecord(stream, FamilyLocal, "myhost", "5", "MIT-MAGIC-COOKIE-1", new byte[16]);
        stream.Position = 0;

        XauthCookieStore.ReadCookie(stream, displayNumber: 0).Should().BeNull();
    }
}
