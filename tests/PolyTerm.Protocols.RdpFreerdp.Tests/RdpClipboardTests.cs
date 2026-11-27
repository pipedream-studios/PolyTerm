using System.Text;
using AwesomeAssertions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.RdpFreerdp.Tests;

/// <summary>The text conversion and echo rules behind RDP clipboard sync (issue #389).</summary>
public sealed class RdpClipboardTests
{
    private static ClipboardPayload Local(string text) => new() { Text = text };

    /// <summary><c>CF_UNICODETEXT</c> as the remote sends it, terminator included.</summary>
    private static byte[] Remote(string text) => Encoding.Unicode.GetBytes(text + "\0");

    [Fact]
    public void PrepareToSend_EncodesAsNulTerminatedUtf16()
    {
        var data = new RdpClipboard("\n").PrepareToSend(Local("héllo ✓ 😀"));

        data.Should().Equal(Remote("héllo ✓ 😀"));
    }

    [Theory]
    [InlineData("one\ntwo", "one\r\ntwo")]
    [InlineData("one\r\ntwo", "one\r\ntwo")]
    [InlineData("one\r\ntwo\nthree\n", "one\r\ntwo\r\nthree\r\n")]
    [InlineData("one\rtwo", "one\rtwo")]
    public void PrepareToSend_UsesCrlfLineEndings(string local, string expected)
    {
        var data = new RdpClipboard("\n").PrepareToSend(Local(local));

        data.Should().Equal(Remote(expected));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Receive_UsesTheLocalLineEnding(string localNewLine)
    {
        var payload = new RdpClipboard(localNewLine).Receive(Remote("one\r\ntwo\nthree"));

        payload!.Text.Should().Be($"one{localNewLine}two{localNewLine}three");
        payload.MimeType.Should().Be("text/plain");
    }

    [Fact]
    public void Receive_DecodesCharactersOutsideTheBasicPlane()
    {
        var payload = new RdpClipboard("\n").Receive(Remote("héllo ✓ 漢字 😀"));

        payload!.Text.Should().Be("héllo ✓ 漢字 😀");
    }

    [Fact]
    public void Receive_StopsAtTheFirstNul()
    {
        var payload = new RdpClipboard("\n").Receive(Remote("kept\0leftover"));

        payload!.Text.Should().Be("kept");
    }

    [Fact]
    public void Receive_AcceptsDataWithoutATerminator()
    {
        var payload = new RdpClipboard("\n").Receive(Encoding.Unicode.GetBytes("unterminated"));

        payload!.Text.Should().Be("unterminated");
    }

    [Fact]
    public void Receive_DropsAStrayOddByte()
    {
        byte[] data = [.. Encoding.Unicode.GetBytes("odd"), 0x41];

        var payload = new RdpClipboard("\n").Receive(data);

        payload!.Text.Should().Be("odd");
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x41 })]
    [InlineData(new byte[] { 0, 0 })]
    public void Receive_IgnoresDataWithNoText(byte[] data) =>
        new RdpClipboard("\n").Receive(data).Should().BeNull();

    [Fact]
    public void Receive_AcceptsDataAtTheSizeLimitAndIgnoresAnythingOverIt()
    {
        var atLimit = Encoding.Unicode.GetBytes(new string('a', RdpClipboard.MaxRemoteBytes / 2));
        byte[] overLimit = [.. atLimit, 0, 0];

        new RdpClipboard("\n").Receive(overLimit).Should().BeNull();
        new RdpClipboard("\n").Receive(atLimit).Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0hidden")]
    public void PrepareToSend_IgnoresEmptyText(string text) =>
        new RdpClipboard("\n").PrepareToSend(Local(text)).Should().BeNull();

    [Fact]
    public void PrepareToSend_IgnoresOtherMimeTypes()
    {
        var payload = new ClipboardPayload { Text = "<b>bold</b>", MimeType = "text/html" };

        new RdpClipboard("\n").PrepareToSend(payload).Should().BeNull();
    }

    [Fact]
    public void PrepareToSend_SendsUnchangedTextOnlyOnce()
    {
        var clipboard = new RdpClipboard("\n");

        clipboard.PrepareToSend(Local("same")).Should().NotBeNull();
        clipboard.PrepareToSend(Local("same")).Should().BeNull();
        clipboard.PrepareToSend(Local("different")).Should().NotBeNull();
        clipboard.PrepareToSend(Local("same")).Should().NotBeNull("it changed back, so the remote has something else");
    }

    [Fact]
    public void Receive_RaisesARepeatedAnnouncementOnlyOnce()
    {
        var clipboard = new RdpClipboard("\n");

        clipboard.Receive(Remote("copied")).Should().NotBeNull();
        clipboard.Receive(Remote("copied")).Should().BeNull("Windows announces each copy twice");
        clipboard.Receive(Remote("copied again")).Should().NotBeNull();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void PrepareToSend_NeverSendsReceivedTextBack(string localNewLine)
    {
        var clipboard = new RdpClipboard(localNewLine);
        var received = clipboard.Receive(Remote("one\r\ntwo"));

        clipboard.PrepareToSend(Local(received!.Text)).Should().BeNull();
    }

    [Fact]
    public void Receive_IgnoresTheRemoteOfferingBackWhatWasSent()
    {
        var clipboard = new RdpClipboard("\r\n");
        clipboard.PrepareToSend(Local("one\ntwo"));

        clipboard.Receive(Remote("one\r\ntwo")).Should().BeNull();
    }

    [Fact]
    public void PrepareToSend_SendsTextCopiedAfterTheRemoteCopiedSomething()
    {
        var clipboard = new RdpClipboard("\n");
        clipboard.PrepareToSend(Local("mine"));
        clipboard.Receive(Remote("theirs"));

        clipboard.PrepareToSend(Local("mine")).Should().NotBeNull();
    }
}
