using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Telnet;

/// <summary>Describes the rlogin protocol module and creates <see cref="RloginSession"/> instances.</summary>
public sealed class RloginProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public RloginProtocolDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "rlogin";
    public string DisplayName => "Rlogin";
    public string Icon => "🔓";
    public int DefaultPort => 513;
    public SurfaceKind Surface => SurfaceKind.Terminal;

    public ProtocolCapabilities Capabilities =>
        ProtocolCapabilities.Terminal | ProtocolCapabilities.InputBroadcast | ProtocolCapabilities.Recording;

    public SettingsSchema Settings { get; } = new()
    {
        Fields =
        [
            new() { Key = "username",      DisplayName = "Remote Username", Type = SettingsFieldType.Text, Group = "Connection" },
            new() { Key = "localUsername", DisplayName = "Local Username",  Type = SettingsFieldType.Text, Group = "Connection" },
        ],
    };

    public IProtocolSession CreateSession(SessionContext ctx) =>
        new RloginSession(ctx, _loggerFactory.CreateLogger<RloginSession>());
}
