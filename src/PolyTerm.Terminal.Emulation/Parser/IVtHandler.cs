namespace PolyTerm.Terminal.Parser;

/// <summary>
/// Receives decoded terminal actions from the VT parser.
/// All calls are synchronous and happen on the engine I/O thread.
/// </summary>
public interface IVtHandler
{
    /// <summary>Printable ASCII character.</summary>
    void Print(char ch);

    /// <summary>Unicode codepoint (from multi-byte UTF-8).</summary>
    void PrintRune(int codepoint);

    /// <summary>C0/C1 control character (e.g. 0x07 BEL, 0x08 BS, 0x0D CR, 0x0A LF).</summary>
    void Execute(byte b);

    /// <summary>ESC sequence dispatched.</summary>
    void EscDispatch(ReadOnlySpan<byte> intermediates, byte finalByte);

    /// <summary>CSI sequence dispatched.</summary>
    void CsiDispatch(ReadOnlySpan<int> parameters, ReadOnlySpan<byte> intermediates, byte finalByte);

    /// <summary>OSC sequence (code already parsed from the string).</summary>
    void OscDispatch(int code, string data);
}
