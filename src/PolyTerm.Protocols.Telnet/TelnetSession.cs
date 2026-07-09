using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Telnet;

/// <summary>A Telnet session — connects a TCP socket and wraps it in a <see cref="TelnetTerminalChannel"/>.</summary>
internal sealed class TelnetSession : TcpSessionBase
{
    public TelnetSession(SessionContext ctx, ILogger<TelnetSession> logger) : base(ctx, logger) { }

    protected override async Task<TcpSocketTerminalChannel> ConnectChannelAsync(CancellationToken ct)
    {
        var host = Ctx.Profile.Host ?? throw new InvalidOperationException("No host configured.");
        var port = Ctx.Profile.Port is > 0 ? Ctx.Profile.Port.Value : 23;

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
        return new TelnetTerminalChannel(socket);
    }
}
