using Microsoft.Extensions.Logging;
using PolyTerm.Engine;
using PolyTerm.Protocols.Ssh.X11;

namespace PolyTerm.Protocols.Ssh;

/// <summary>Describes the SSH protocol module's capabilities and settings, and creates <see cref="SshSession"/> instances.</summary>
public sealed class SshProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IBundledX11ServerProvider _bundledX11Server;

    public SshProtocolDescriptor(ILoggerFactory loggerFactory, IBundledX11ServerProvider bundledX11Server)
    {
        _loggerFactory = loggerFactory;
        _bundledX11Server = bundledX11Server;
    }

    public string Id => "ssh";
    public string DisplayName => "SSH";
    public string Icon => "🔐";
    public int DefaultPort => 22;
    public SurfaceKind Surface => SurfaceKind.Terminal;

    /// <summary>SSH supports a terminal, SFTP file transfer, port/X11 forwarding, exec, recording, and auto-reconnect.</summary>
    public ProtocolCapabilities Capabilities =>
        ProtocolCapabilities.Terminal      |
        ProtocolCapabilities.FileTransfer  |
        ProtocolCapabilities.PortForwarding|
        ProtocolCapabilities.X11Forwarding |
        ProtocolCapabilities.Exec          |
        ProtocolCapabilities.InputBroadcast|
        ProtocolCapabilities.Recording     |
        ProtocolCapabilities.Reconnect;

    public SettingsSchema Settings { get; } = BuildSchema();

    /// <summary>Creates a new, not-yet-connected <see cref="SshSession"/> bound to the given context.</summary>
    public IProtocolSession CreateSession(SessionContext ctx) =>
        new SshSession(ctx, _loggerFactory.CreateLogger<SshSession>(), _bundledX11Server);

    private static SettingsSchema BuildSchema() => new()
    {
        Fields =
        [
            new() { Key = "username",        DisplayName = "Username",           Type = SettingsFieldType.Text,  Required = true,  Group = "Connection" },
            new() { Key = "privateKeyPath",   DisplayName = "Private Key File",   Type = SettingsFieldType.FilePath, Group = "Auth" },
            new() { Key = "agentForwarding",  DisplayName = "Agent Forwarding",   Type = SettingsFieldType.Boolean, Group = "Auth" },
            new() { Key = "keepAliveSeconds", DisplayName = "Keepalive (seconds)",Type = SettingsFieldType.Number, Group = "Connection", Default = System.Text.Json.JsonSerializer.SerializeToElement(30) },
            new() { Key = "terminalType",     DisplayName = "Terminal Type",       Type = SettingsFieldType.Text,  Group = "Terminal", Default = System.Text.Json.JsonSerializer.SerializeToElement("xterm-256color") },
            new() { Key = "x11Forwarding",    DisplayName = "X11 Forwarding",     Type = SettingsFieldType.Boolean, Group = "Tunneling", RequiredCapabilities = ProtocolCapabilities.X11Forwarding },
        ]
    };
}
