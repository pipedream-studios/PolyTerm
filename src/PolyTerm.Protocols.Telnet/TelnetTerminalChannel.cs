using System.Net.Sockets;

namespace PolyTerm.Protocols.Telnet;

/// <summary>
/// Minimal Telnet (RFC 854) client: strips/answers IAC option-negotiation sequences rather than
/// passing them through to the terminal emulator, and sends NAWS (window size) on resize. Deliberately
/// not RFC-complete — "own implementation (trivial protocols)" per the spec: refuses every option a
/// server proposes (<c>DONT</c>/<c>WONT</c>), which is a legitimate minimal-client stance per RFC 854
/// ("a party may... refuse ...") and is enough for a server to drop into plain line/character mode
/// rather than hang waiting for a negotiated feature.
/// </summary>
internal sealed class TelnetTerminalChannel : TcpSocketTerminalChannel
{
    private const byte IAC  = 255;
    private const byte DONT = 254;
    private const byte DO   = 253;
    private const byte WONT = 252;
    private const byte WILL = 251;
    private const byte SB   = 250;
    private const byte SE   = 240;
    private const byte NAWS = 31;

    private enum State { Data, Iac, Command, Subnegotiation, SubnegotiationIac }
    private State _state = State.Data;
    private byte _pendingCommand;

    public TelnetTerminalChannel(Socket socket) : base(socket, "xterm-256color") { }

    protected override ReadOnlyMemory<byte> ProcessIncoming(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        List<byte>? output = null; // only allocated if this chunk actually contains IAC sequences

        for (var i = 0; i < span.Length; i++)
        {
            var b = span[i];
            switch (_state)
            {
                case State.Data:
                    if (b == IAC) { output ??= CopyPrefix(span, i); _state = State.Iac; }
                    else output?.Add(b);
                    break;

                case State.Iac:
                    if (b == IAC) { output!.Add(IAC); _state = State.Data; } // escaped literal 0xFF
                    else if (b is WILL or WONT or DO or DONT) { _pendingCommand = b; _state = State.Command; }
                    else if (b == SB) { _state = State.Subnegotiation; }
                    else { _state = State.Data; } // other 2-byte commands (NOP/GA/etc.) — ignore
                    break;

                case State.Command:
                    RespondToOption(_pendingCommand, b);
                    _state = State.Data;
                    break;

                case State.Subnegotiation:
                    if (b == IAC) _state = State.SubnegotiationIac;
                    break; // subnegotiation payload is discarded — we never request any option that has one

                case State.SubnegotiationIac:
                    _state = b == SE ? State.Data : State.Subnegotiation;
                    break;
            }
        }

        return output is null ? data : new ReadOnlyMemory<byte>(output.ToArray());
    }

    private static List<byte> CopyPrefix(ReadOnlySpan<byte> span, int uptoExclusive)
    {
        var list = new List<byte>(span.Length);
        for (var j = 0; j < uptoExclusive; j++) list.Add(span[j]);
        return list;
    }

    /// <summary>
    /// Refuses everything: <c>DONT</c> in reply to <c>WILL</c>, <c>WONT</c> in reply to <c>DO</c>.
    /// Per RFC 854, a reply is only required for WILL/DO (never for WONT/DONT, to avoid infinite
    /// negotiation loops), so those are intentionally not answered here.
    /// </summary>
    private void RespondToOption(byte command, byte option)
    {
        if (command != WILL && command != DO) return;
        Span<byte> reply = [IAC, command == WILL ? DONT : WONT, option];
        Stream.Write(reply);
    }

    public override async ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        // IAC SB NAWS <cols-hi> <cols-lo> <rows-hi> <rows-lo> IAC SE
        byte[] naws =
        [
            IAC, SB, NAWS,
            (byte)(cols >> 8), (byte)(cols & 0xFF),
            (byte)(rows >> 8), (byte)(rows & 0xFF),
            IAC, SE,
        ];
        await Stream.WriteAsync(naws, ct).ConfigureAwait(false);
    }
}
