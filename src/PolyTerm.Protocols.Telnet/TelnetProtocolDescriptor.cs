using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Telnet;

/// <summary>Describes the Telnet protocol module and creates <see cref="TelnetSession"/> instances.</summary>
public sealed class TelnetProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public TelnetProtocolDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "telnet";
    public string DisplayName => "Telnet";
    public int DefaultPort => 23;
    public SurfaceKind Surface => SurfaceKind.Terminal;

    public ProtocolCapabilities Capabilities =>
        ProtocolCapabilities.Terminal | ProtocolCapabilities.InputBroadcast | ProtocolCapabilities.Recording;

    public SettingsSchema Settings => SettingsSchema.Empty;

    public IProtocolSession CreateSession(SessionContext ctx) =>
        new TelnetSession(ctx, _loggerFactory.CreateLogger<TelnetSession>());
}
