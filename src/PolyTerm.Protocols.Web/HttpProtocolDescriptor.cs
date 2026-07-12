using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Web;

/// <summary>Describes the plain-HTTP protocol — embeds the profile's URL in a browser tab by default.</summary>
public sealed class HttpProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public HttpProtocolDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "http";
    public string DisplayName => "HTTP";
    public int DefaultPort => 80;
    public SurfaceKind Surface => SurfaceKind.WebView;
    public ProtocolCapabilities Capabilities => ProtocolCapabilities.WebContent;
    public SettingsSchema Settings { get; } = BuildSchema();

    public IProtocolSession CreateSession(SessionContext ctx) =>
        new WebSession(ctx, "http", _loggerFactory.CreateLogger<WebSession>());

    private static SettingsSchema BuildSchema() => new()
    {
        Fields =
        [
            new() { Key = "openExternally", DisplayName = "Open in external browser instead", Type = SettingsFieldType.Boolean,
                    Description = "Opens this connection in your default web browser instead of an embedded tab.",
                    Default = JsonSerializer.SerializeToElement(false) },
        ]
    };
}
