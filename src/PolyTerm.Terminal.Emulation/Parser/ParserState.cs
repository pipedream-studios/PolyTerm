namespace PolyTerm.Terminal.Parser;

/// <summary>
/// States of the VT/ANSI byte-stream parser state machine (based on Paul Williams' DEC ANSI
/// parser: https://vt100.net/emu/dec_ansi_parser). Each state represents where the parser is
/// within a control sequence; see <see cref="VtParser"/> for the transition logic.
/// </summary>
internal enum ParserState
{
    /// <summary>Default state: plain printable characters and C0 controls are handled directly.</summary>
    Ground,

    /// <summary>Just consumed ESC (0x1B); waiting to see whether this starts CSI, OSC, DCS, or a bare escape sequence.</summary>
    Escape,

    /// <summary>Consuming intermediate bytes (0x20-0x2F) of a bare escape sequence before its final byte.</summary>
    EscapeIntermediate,

    /// <summary>Just consumed CSI (ESC [ or 0x9B); waiting for parameters, intermediates, or the final byte.</summary>
    CsiEntry,

    /// <summary>Consuming numeric parameter bytes of a CSI sequence.</summary>
    CsiParam,

    /// <summary>Consuming intermediate bytes (0x20-0x2F) of a CSI sequence after its parameters.</summary>
    CsiIntermediate,

    /// <summary>A malformed/unsupported CSI sequence is being discarded until its final byte.</summary>
    CsiIgnore,

    /// <summary>Just consumed DCS (ESC P or 0x90); waiting for parameters or intermediates.</summary>
    DcsEntry,

    /// <summary>Consuming numeric parameter bytes of a DCS sequence.</summary>
    DcsParam,

    /// <summary>Consuming intermediate bytes of a DCS sequence after its parameters.</summary>
    DcsIntermediate,

    /// <summary>Inside the data portion of a DCS sequence, passed through until the string terminator.</summary>
    DcsPassthrough,

    /// <summary>A malformed/unsupported DCS sequence is being discarded until the string terminator.</summary>
    DcsIgnore,

    /// <summary>Accumulating an OSC (Operating System Command) string until BEL or ST terminates it.</summary>
    OscString,

    /// <summary>Discarding an SOS/PM/APC string until the string terminator (contents are not interpreted).</summary>
    SosPmApcString,

    /// <summary>Accumulating continuation bytes of a multi-byte UTF-8 codepoint.</summary>
    Utf8,
}
