using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Vnc;

/// <summary>Describes the VNC protocol module and creates <see cref="VncSession"/> instances.</summary>
public sealed class VncProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public VncProtocolDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "vnc";
    public string DisplayName => "VNC";
    public int DefaultPort => 5900;
    public SurfaceKind Surface => SurfaceKind.Framebuffer;
    public ProtocolCapabilities Capabilities => ProtocolCapabilities.GraphicalDesktop;
    public SettingsSchema Settings => SettingsSchema.Empty;

    public IProtocolSession CreateSession(SessionContext ctx) => new VncSession(ctx, _loggerFactory);
}
