using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Telnet;

/// <summary>
/// An rlogin (RFC 1282) session: sends the one-time null-delimited handshake
/// (<c>client-user\0server-user\0terminal-type/speed\0</c>) and waits for the server's single-byte
/// zero ack, then becomes plain pass-through. Deliberately minimal — doesn't implement rlogin's
/// optional out-of-band flow-control negotiation, and doesn't (can't, from an unprivileged desktop
/// client) satisfy the traditional "connect from a privileged source port" trusted-hosts auth model,
/// so this only works against servers that accept a username/password-style login instead.
/// </summary>
internal sealed class RloginSession : TcpSessionBase
{
    private const string TerminalType = "xterm-256color/38400";

    public RloginSession(SessionContext ctx, ILogger<RloginSession> logger) : base(ctx, logger) { }

    protected override async Task<TcpSocketTerminalChannel> ConnectChannelAsync(CancellationToken ct)
    {
        var host = Ctx.Profile.Host ?? throw new InvalidOperationException("No host configured.");
        var port = Ctx.Profile.Port is > 0 ? Ctx.Profile.Port.Value : 513;
        var remoteUser = GetSetting(Ctx.Profile.Settings, "username") ?? Ctx.Credential?.Username ?? Environment.UserName;
        var localUser  = GetSetting(Ctx.Profile.Settings, "localUsername") ?? Environment.UserName;

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);

        var handshake = Encoding.ASCII.GetBytes($"\0{localUser}\0{remoteUser}\0{TerminalType}\0");
        await socket.SendAsync(handshake, SocketFlags.None, ct).ConfigureAwait(false);

        var ack = new byte[1];
        var received = await socket.ReceiveAsync(ack, SocketFlags.None, ct).ConfigureAwait(false);
        if (received == 1 && ack[0] != 0)
            throw new InvalidOperationException("Unexpected rlogin handshake response.");

        return new TcpSocketTerminalChannel(socket, "xterm-256color");
    }

    private static string? GetSetting(Dictionary<string, JsonElement> settings, string key) =>
        settings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
