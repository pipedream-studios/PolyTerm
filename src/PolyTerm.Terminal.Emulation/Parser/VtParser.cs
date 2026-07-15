using System.Text;

namespace PolyTerm.Terminal.Parser;

/// <summary>
/// Implementation of the Paul Williams VT parser state machine.
/// See: https://vt100.net/emu/dec_ansi_parser
/// Converts a raw byte stream into discrete terminal actions delivered to an <see cref="IVtHandler"/>.
/// </summary>
public sealed class VtParser
{
    private ParserState _state = ParserState.Ground;

    private readonly int[] _params = new int[32];
    private int _paramCount;
    private bool _paramHasValue;

    private readonly byte[] _intermediates = new byte[4];
    private int _intermediateCount;

    private readonly StringBuilder _oscBuf = new(256);

    // UTF-8 multi-byte accumulation
    private int _utf8Remaining;
    private int _utf8Codepoint;

    private readonly IVtHandler _handler;

    /// <summary>Creates a parser that delivers decoded actions to <paramref name="handler"/>.</summary>
    public VtParser(IVtHandler handler) => _handler = handler;

    /// <summary>Feeds a chunk of raw terminal output bytes through the state machine, dispatching actions to the handler as they're recognized.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            ProcessByte(b);
    }

    private void ProcessByte(byte b)
    {
        // Handle C0 controls that are always executed regardless of state
        if (b == 0x18 || b == 0x1A)  { Execute(b); Transition(ParserState.Ground); return; }
        if (b == 0x1B)               { Transition(ParserState.Escape); return; }
        if (b == 0x9B)               { Transition(ParserState.CsiEntry); return; } // 8-bit CSI
        if (b == 0x90)               { Transition(ParserState.DcsEntry); return; } // 8-bit DCS
        if (b == 0x9D)               { Transition(ParserState.OscString); _oscBuf.Clear(); return; } // 8-bit OSC

        switch (_state)
        {
            case ParserState.Ground:
                Ground(b);
                break;
            case ParserState.Escape:
                Escape(b);
                break;
            case ParserState.EscapeIntermediate:
                EscapeIntermediate(b);
                break;
            case ParserState.CsiEntry:
                CsiEntry(b);
                break;
            case ParserState.CsiParam:
                CsiParam(b);
                break;
            case ParserState.CsiIntermediate:
                CsiIntermediate(b);
                break;
            case ParserState.CsiIgnore:
                if (b == 0x40 || (b >= 0x40 && b <= 0x7E)) Transition(ParserState.Ground);
                break;
            case ParserState.OscString:
                OscString(b);
                break;
            case ParserState.DcsEntry:
            case ParserState.DcsParam:
            case ParserState.DcsPassthrough:
            case ParserState.DcsIgnore:
                Dcs(b);
                break;
            case ParserState.Utf8:
                ContinueUtf8(b);
                break;
        }
    }

    private void Ground(byte b)
    {
        if (b >= 0x80 && b < 0xC0)
        {
            // C1 control or continuation byte outside multi-byte sequence — ignore
        }
        else if (b >= 0xC0)
        {
            StartUtf8(b);
        }
        else if (b < 0x20 || b == 0x7F)
        {
            Execute(b);
        }
        else
        {
            _handler.Print((char)b);
        }
    }

    private void StartUtf8(byte b)
    {
        if ((b & 0xE0) == 0xC0)      { _utf8Remaining = 1; _utf8Codepoint = b & 0x1F; }
        else if ((b & 0xF0) == 0xE0) { _utf8Remaining = 2; _utf8Codepoint = b & 0x0F; }
        else if ((b & 0xF8) == 0xF0) { _utf8Remaining = 3; _utf8Codepoint = b & 0x07; }
        else return; // not a valid lead byte
        _state = ParserState.Utf8;
    }

    private void ContinueUtf8(byte b)
    {
        if ((b & 0xC0) != 0x80) { _state = ParserState.Ground; Ground(b); return; }
        _utf8Codepoint = (_utf8Codepoint << 6) | (b & 0x3F);
        if (--_utf8Remaining == 0)
        {
            _state = ParserState.Ground;
            if (_utf8Codepoint <= 0x10FFFF)
                _handler.PrintRune(_utf8Codepoint);
        }
    }

    private void Escape(byte b)
    {
        _intermediateCount = 0;
        if (b >= 0x20 && b <= 0x2F)         { _intermediates[_intermediateCount++] = b; Transition(ParserState.EscapeIntermediate); }
        else if (b == 0x5B)                  { ClearParams(); Transition(ParserState.CsiEntry); }
        else if (b == 0x5D)                  { _oscBuf.Clear(); Transition(ParserState.OscString); }
        else if (b == 0x50)                  { Transition(ParserState.DcsEntry); }
        else if (b >= 0x30 && b <= 0x7E)     { EscDispatch(b); Transition(ParserState.Ground); }
        else if (b < 0x20)                   { Execute(b); }
        else                                 { Transition(ParserState.Ground); }
    }

    private void EscapeIntermediate(byte b)
    {
        if (b >= 0x20 && b <= 0x2F)      { if (_intermediateCount < _intermediates.Length) _intermediates[_intermediateCount++] = b; }
        else if (b >= 0x30 && b <= 0x7E) { EscDispatch(b); Transition(ParserState.Ground); }
        else if (b < 0x20)               { Execute(b); }
        else                             { Transition(ParserState.Ground); }
    }

    private void CsiEntry(byte b)
    {
        ClearParams();
        _intermediateCount = 0;
        // Private parameter prefix (<=>?) is only legal as the very first byte after CSI per ECMA-48 -
        // route it into _intermediates (what ApplyMode's isPrivate check reads), not AccumulateParam,
        // which silently discards it: CsiDispatch's caller otherwise has no way to tell "CSI ?1h"
        // (DECCKM, a private mode) apart from "CSI 1h" (a different, non-private mode 1).
        if (b >= 0x3C && b <= 0x3F)     { _intermediates[_intermediateCount++] = b; Transition(ParserState.CsiParam); }
        else if (b >= 0x40 && b <= 0x7E)     { CsiDispatch(b); Transition(ParserState.Ground); }
        else if (b >= 0x30 && b <= 0x3F){ AccumulateParam(b); Transition(ParserState.CsiParam); }
        else if (b >= 0x20 && b <= 0x2F){ _intermediates[_intermediateCount++] = b; Transition(ParserState.CsiIntermediate); }
        else if (b == 0x3A)             { Transition(ParserState.CsiIgnore); } // colon (sub-params, not fully impl)
        else if (b < 0x20)              { Execute(b); }
    }

    private void CsiParam(byte b)
    {
        if (b >= 0x30 && b <= 0x3F)      { AccumulateParam(b); }
        else if (b >= 0x40 && b <= 0x7E) { CsiDispatch(b); Transition(ParserState.Ground); }
        else if (b >= 0x20 && b <= 0x2F) { _intermediates[_intermediateCount++] = b; Transition(ParserState.CsiIntermediate); }
        else if (b == 0x3A)              { Transition(ParserState.CsiIgnore); }
        else if (b < 0x20)               { Execute(b); }
    }

    private void CsiIntermediate(byte b)
    {
        if (b >= 0x40 && b <= 0x7E) { CsiDispatch(b); Transition(ParserState.Ground); }
        else if (b >= 0x20 && b <= 0x2F) { if (_intermediateCount < _intermediates.Length) _intermediates[_intermediateCount++] = b; }
        else if (b >= 0x30 && b <= 0x3F) { Transition(ParserState.CsiIgnore); }
        else if (b < 0x20) { Execute(b); }
    }

    private void OscString(byte b)
    {
        if (b == 0x07 || b == 0x9C) // BEL or ST terminates OSC
        {
            OscDispatch();
            Transition(ParserState.Ground);
        }
        else if (b == 0x1B) // could be ESC \
        {
            // peek-ahead not possible without buffering; treat as ESC → ST follows
            OscDispatch();
            Transition(ParserState.Escape);
        }
        else if (b >= 0x20)
        {
            _oscBuf.Append((char)b);
        }
    }

    private void Dcs(byte b)
    {
        // Minimal DCS: just absorb until ST (0x9C) or ESC \
        if (b == 0x9C || b == 0x07) Transition(ParserState.Ground);
        else if (b == 0x1B) Transition(ParserState.Escape);
    }

    private void Execute(byte b) => _handler.Execute(b);

    private void EscDispatch(byte b) =>
        _handler.EscDispatch(_intermediates[.._intermediateCount], b);

    private void CsiDispatch(byte b) =>
        _handler.CsiDispatch(
            _params[..(_paramCount + 1)],
            _intermediates[.._intermediateCount],
            b);

    private void OscDispatch()
    {
        var raw = _oscBuf.ToString();
        var semi = raw.IndexOf(';');
        if (semi < 0) { _handler.OscDispatch(0, raw); return; }
        if (int.TryParse(raw.AsSpan(0, semi), out var code))
            _handler.OscDispatch(code, raw[(semi + 1)..]);
        _oscBuf.Clear();
    }

    private void ClearParams()
    {
        _paramCount = 0;
        _paramHasValue = false;
        _params[0] = 0;
    }

    private void AccumulateParam(byte b)
    {
        if (b == 0x3B) // ';' param separator
        {
            if (!_paramHasValue) _params[_paramCount] = 0;
            if (_paramCount < _params.Length - 1) _paramCount++;
            _params[_paramCount] = 0;
            _paramHasValue = false;
        }
        else if (b >= 0x30 && b <= 0x39) // '0'..'9'
        {
            _params[_paramCount] = _params[_paramCount] * 10 + (b - 0x30);
            _paramHasValue = true;
        }
        // Ignore '?' (private use) and ':' (sub-params) — stored via intermediate path
    }

    private void Transition(ParserState next) => _state = next;
}
