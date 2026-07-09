using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Serial;

/// <summary>Describes the Serial/COM protocol module and creates <see cref="SerialSession"/> instances.</summary>
public sealed class SerialProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public SerialProtocolDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "serial";
    public string DisplayName => "Serial";

    /// <summary>0 — a serial connection addresses a COM port, not a network port.</summary>
    public int DefaultPort => 0;
    public SurfaceKind Surface => SurfaceKind.Terminal;

    /// <summary>No <see cref="ProtocolCapabilities.InputBroadcast"/> — a serial line usually addresses one physical device, broadcast doesn't make sense.</summary>
    public ProtocolCapabilities Capabilities => ProtocolCapabilities.Terminal | ProtocolCapabilities.Recording;

    public SettingsSchema Settings { get; } = new()
    {
        Fields =
        [
            new() { Key = "portName", DisplayName = "Port",     Type = SettingsFieldType.Text,   Required = true, Group = "Connection" },
            new() { Key = "baudRate", DisplayName = "Baud Rate", Type = SettingsFieldType.Number, Group = "Connection", Default = JsonSerializer.SerializeToElement(9600) },
            new() { Key = "dataBits", DisplayName = "Data Bits", Type = SettingsFieldType.Number, Group = "Connection", Default = JsonSerializer.SerializeToElement(8) },
            new() { Key = "parity",   DisplayName = "Parity",    Type = SettingsFieldType.Enum,   Description = "None, Odd, Even, Mark, Space", Group = "Connection", Default = JsonSerializer.SerializeToElement("None") },
            new() { Key = "stopBits", DisplayName = "Stop Bits", Type = SettingsFieldType.Enum,   Description = "One, OnePointFive, Two",       Group = "Connection", Default = JsonSerializer.SerializeToElement("One") },
        ],
    };

    public IProtocolSession CreateSession(SessionContext ctx) =>
        new SerialSession(ctx, _loggerFactory.CreateLogger<SerialSession>());
}
