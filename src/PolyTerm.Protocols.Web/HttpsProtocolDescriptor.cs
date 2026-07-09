using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Web;

/// <summary>Describes the HTTPS launcher protocol — opens the profile's URL in the OS default browser.</summary>
public sealed class HttpsProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public HttpsProtocolDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "https";
    public string DisplayName => "HTTPS";
    public int DefaultPort => 443;
    public SurfaceKind Surface => SurfaceKind.External;
    public ProtocolCapabilities Capabilities => ProtocolCapabilities.None;
    public SettingsSchema Settings => SettingsSchema.Empty;

    public IProtocolSession CreateSession(SessionContext ctx) =>
        new WebSession(ctx, "https", _loggerFactory.CreateLogger<WebSession>());
}
