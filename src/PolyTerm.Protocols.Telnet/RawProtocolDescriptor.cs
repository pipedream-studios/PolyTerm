using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Telnet;

/// <summary>Describes the raw-TCP protocol module (no framing at all) and creates <see cref="RawSession"/> instances.</summary>
public sealed class RawProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public RawProtocolDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "raw";
    public string DisplayName => "Raw TCP";
    public string Icon => "📟";

    /// <summary>0 — no sensible default for an arbitrary raw socket; the user must specify one.</summary>
    public int DefaultPort => 0;
    public SurfaceKind Surface => SurfaceKind.Terminal;

    public ProtocolCapabilities Capabilities =>
        ProtocolCapabilities.Terminal | ProtocolCapabilities.InputBroadcast | ProtocolCapabilities.Recording;

    public SettingsSchema Settings => SettingsSchema.Empty;

    public IProtocolSession CreateSession(SessionContext ctx) =>
        new RawSession(ctx, _loggerFactory.CreateLogger<RawSession>());
}
