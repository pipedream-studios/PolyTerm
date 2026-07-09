using FluentAssertions;
using PolyTerm.Terminal;
using PolyTerm.Terminal.Parser;
using Xunit;

namespace PolyTerm.Terminal.Tests;

public sealed class VtParserTests
{
    private readonly RecordingHandler _handler = new();
    private readonly VtParser _parser;

    public VtParserTests() => _parser = new VtParser(_handler);

    [Fact]
    public void PrintAscii_EmitsChars()
    {
        _parser.Feed("Hello"u8);
        _handler.Printed.Should().Equal('H', 'e', 'l', 'l', 'o');
    }

    [Fact]
    public void Lf_EmitsExecute()
    {
        _parser.Feed("\n"u8);
        _handler.Executed.Should().Contain(0x0A);
    }

    [Fact]
    public void CsiCursorUp_Dispatched()
    {
        _parser.Feed("\x1b[3A"u8); // CSI 3 A = cursor up 3
        _handler.CsiDispatches.Should().ContainSingle();
        var d = _handler.CsiDispatches[0];
        d.Final.Should().Be((byte)'A');
        d.Params.Should().Equal([3]);
    }

    [Fact]
    public void CsiSgr_Reset_Dispatched()
    {
        _parser.Feed("\x1b[0m"u8);
        _handler.CsiDispatches.Should().ContainSingle(d => d.Final == (byte)'m' && d.Params[0] == 0);
    }

    [Fact]
    public void EscSaveCursor_Dispatched()
    {
        _parser.Feed([0x1B, (byte)'7']); // "\x1b7" is greedy (parses U+01B7), must use bytes
        _handler.EscDispatches.Should().ContainSingle(d => d.Final == (byte)'7');
    }

    [Fact]
    public void Osc_TitleSet_Dispatched()
    {
        _parser.Feed("\x1b]2;My Title\x07"u8);
        _handler.OscDispatches.Should().ContainSingle(d => d.Code == 2 && d.Data == "My Title");
    }

    [Fact]
    public void Utf8_MultiByte_PrintsRune()
    {
        // U+00E9 = é = 0xC3 0xA9
        _parser.Feed([0xC3, 0xA9]);
        _handler.PrintedRunes.Should().Contain(0xE9);
    }

    [Fact]
    public void CsiWithMultipleParams()
    {
        _parser.Feed("\x1b[1;31m"u8); // SGR bold red
        var d = _handler.CsiDispatches.Should().ContainSingle().Subject;
        d.Final.Should().Be((byte)'m');
        d.Params.Should().Equal([1, 31]);
    }
}

// ─── Test double ──────────────────────────────────────────────────────────────

internal sealed class RecordingHandler : IVtHandler
{
    public List<char> Printed { get; } = [];
    public List<int> PrintedRunes { get; } = [];
    public List<byte> Executed { get; } = [];
    public List<(byte Final, int[] Params)> CsiDispatches { get; } = [];
    public List<(byte Final, byte[] Intermediates)> EscDispatches { get; } = [];
    public List<(int Code, string Data)> OscDispatches { get; } = [];

    public void Print(char ch) => Printed.Add(ch);
    public void PrintRune(int codepoint) => PrintedRunes.Add(codepoint);
    public void Execute(byte b) => Executed.Add(b);

    public void CsiDispatch(ReadOnlySpan<int> parameters, ReadOnlySpan<byte> intermediates, byte finalByte) =>
        CsiDispatches.Add((finalByte, parameters.ToArray()));

    public void EscDispatch(ReadOnlySpan<byte> intermediates, byte finalByte) =>
        EscDispatches.Add((finalByte, intermediates.ToArray()));

    public void OscDispatch(int code, string data) =>
        OscDispatches.Add((code, data));
}
