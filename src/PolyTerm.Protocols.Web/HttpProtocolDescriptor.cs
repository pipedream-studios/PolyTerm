using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Web;

/// <summary>Describes the plain-HTTP launcher protocol — opens the profile's URL in the OS default browser.</summary>
public sealed class HttpProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public HttpProtocolDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "http";
    public string DisplayName => "HTTP";
    public int DefaultPort => 80;
    public SurfaceKind Surface => SurfaceKind.External;
    public ProtocolCapabilities Capabilities => ProtocolCapabilities.None;
    public SettingsSchema Settings => SettingsSchema.Empty;

    public IProtocolSession CreateSession(SessionContext ctx) =>
        new WebSession(ctx, "http", _loggerFactory.CreateLogger<WebSession>());
}
