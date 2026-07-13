using AwesomeAssertions;
using PolyTerm.Protocols.Ssh.X11;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>Pure byte-rewriting logic — no SSH/network/X11 server involved, unlike the rest of this project's tests.</summary>
public sealed class X11ConnectionSetupRewriterTests
{
    private static byte[] BuildConnClientPrefix(bool bigEndian, string protocolName, byte[] authData, byte[]? trailer = null)
    {
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(protocolName);
        var namePad = (4 - nameBytes.Length % 4) % 4;
        var dataPad = (4 - authData.Length % 4) % 4;

        var buffer = new List<byte>();
        void WriteU16(int value)
        {
            if (bigEndian) { buffer.Add((byte)(value >> 8)); buffer.Add((byte)value); }
            else { buffer.Add((byte)value); buffer.Add((byte)(value >> 8)); }
        }

        buffer.Add(bigEndian ? (byte)0x42 : (byte)0x6C);
        buffer.Add(0); // unused
        WriteU16(11); // protocol-major-version
        WriteU16(0);  // protocol-minor-version
        WriteU16(nameBytes.Length);
        WriteU16(authData.Length);
        WriteU16(0); // unused padding

        buffer.AddRange(nameBytes);
        buffer.AddRange(new byte[namePad]);
        buffer.AddRange(authData);
        buffer.AddRange(new byte[dataPad]);
        if (trailer is not null) buffer.AddRange(trailer);

        return [.. buffer];
    }

    [Fact]
    public void MatchingFakeCookie_BigEndian_IsRewrittenToTheRealCookie()
    {
        var fakeCookie = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var realCookie = Enumerable.Range(100, 16).Select(i => (byte)i).ToArray();
        var packet = BuildConnClientPrefix(bigEndian: true, "MIT-MAGIC-COOKIE-1", fakeCookie, trailer: [1, 2, 3, 4]);

        var result = X11ConnectionSetupRewriter.TryRewriteCookie(packet, fakeCookie, realCookie);

        result.Should().NotBeSameAs(packet, "a rewrite should produce a new array, not mutate the caller's buffer");
        var expected = BuildConnClientPrefix(bigEndian: true, "MIT-MAGIC-COOKIE-1", realCookie, trailer: [1, 2, 3, 4]);
        result.Should().Equal(expected);
    }

    [Fact]
    public void MatchingFakeCookie_LittleEndian_IsRewrittenToTheRealCookie()
    {
        var fakeCookie = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var realCookie = Enumerable.Range(200, 16).Select(i => (byte)i).ToArray();
        var packet = BuildConnClientPrefix(bigEndian: false, "MIT-MAGIC-COOKIE-1", fakeCookie);

        var result = X11ConnectionSetupRewriter.TryRewriteCookie(packet, fakeCookie, realCookie);

        var expected = BuildConnClientPrefix(bigEndian: false, "MIT-MAGIC-COOKIE-1", realCookie);
        result.Should().Equal(expected);
    }

    [Fact]
    public void NoRealCookieAvailable_LeavesThePacketUnchanged()
    {
        var fakeCookie = new byte[16];
        var packet = BuildConnClientPrefix(bigEndian: true, "MIT-MAGIC-COOKIE-1", fakeCookie);

        var result = X11ConnectionSetupRewriter.TryRewriteCookie(packet, fakeCookie, realCookie: null);

        result.Should().BeSameAs(packet, "with no real cookie to substitute, this should be a pure pass-through, not even a defensive copy");
    }

    [Fact]
    public void DifferentAuthDataThanTheFakeCookie_IsLeftUnchanged()
    {
        var fakeCookie = new byte[16];
        var realCookie = new byte[16];
        var someOtherCookie = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        var packet = BuildConnClientPrefix(bigEndian: true, "MIT-MAGIC-COOKIE-1", someOtherCookie);

        var result = X11ConnectionSetupRewriter.TryRewriteCookie(packet, fakeCookie, realCookie);

        result.Should().Equal(packet, "this isn't PolyTerm's own fake cookie, so it must not be touched");
    }

    [Fact]
    public void NonMitMagicCookieProtocol_IsLeftUnchanged()
    {
        var fakeCookie = new byte[16];
        var realCookie = Enumerable.Repeat((byte)0xAA, 16).ToArray();
        var packet = BuildConnClientPrefix(bigEndian: true, "XDM-AUTHORIZATION-1", fakeCookie);

        var result = X11ConnectionSetupRewriter.TryRewriteCookie(packet, fakeCookie, realCookie);

        result.Should().Equal(packet);
    }

    [Fact]
    public void TooShortToBeAConnClientPrefix_IsLeftUnchanged()
    {
        byte[] tinyPacket = [0x42, 0, 0, 11];
        var result = X11ConnectionSetupRewriter.TryRewriteCookie(tinyPacket, new byte[16], new byte[16]);
        result.Should().BeSameAs(tinyPacket);
    }

    [Fact]
    public void UnrecognizedByteOrderMarker_IsLeftUnchanged()
    {
        var packet = BuildConnClientPrefix(bigEndian: true, "MIT-MAGIC-COOKIE-1", new byte[16]);
        packet[0] = 0x99; // neither 'B' nor 'l'

        var result = X11ConnectionSetupRewriter.TryRewriteCookie(packet, new byte[16], new byte[16]);

        result.Should().BeSameAs(packet);
    }

    [Fact]
    public void TruncatedAuthDataField_IsLeftUnchanged()
    {
        // Header claims 16 bytes of auth data but the buffer is cut short before it — a fragmented
        // read, not a real ConnClientPrefix this code should ever try to rewrite.
        var full = BuildConnClientPrefix(bigEndian: true, "MIT-MAGIC-COOKIE-1", new byte[16]);
        var truncated = full[..(full.Length - 10)];

        var result = X11ConnectionSetupRewriter.TryRewriteCookie(truncated, new byte[16], new byte[16]);

        result.Should().BeSameAs(truncated);
    }
}
