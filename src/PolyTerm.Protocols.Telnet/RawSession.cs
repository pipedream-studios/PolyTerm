using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Telnet;

/// <summary>A raw TCP session — no protocol framing at all, bytes pass straight through both directions.</summary>
internal sealed class RawSession : TcpSessionBase
{
    public RawSession(SessionContext ctx, ILogger<RawSession> logger) : base(ctx, logger) { }

    protected override async Task<TcpSocketTerminalChannel> ConnectChannelAsync(CancellationToken ct)
    {
        var host = Ctx.Profile.Host ?? throw new InvalidOperationException("No host configured.");
        var port = Ctx.Profile.Port ?? throw new InvalidOperationException("No port configured.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
        return new TcpSocketTerminalChannel(socket, "dumb");
    }
}
