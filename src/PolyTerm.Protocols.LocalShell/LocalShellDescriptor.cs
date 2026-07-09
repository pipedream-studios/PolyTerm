using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.LocalShell;

/// <summary>Describes the local shell protocol module's capabilities and settings, and creates <see cref="LocalShellSession"/> instances.</summary>
public sealed class LocalShellDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public LocalShellDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "local-shell";
    public string DisplayName => "Local Shell";
    public int DefaultPort => 0;
    public SurfaceKind Surface => SurfaceKind.Terminal;

    /// <summary>Local shell supports only a terminal (no file transfer, forwarding, or reconnect — it's a local process, not a remote link).</summary>
    public ProtocolCapabilities Capabilities =>
        ProtocolCapabilities.Terminal       |
        ProtocolCapabilities.InputBroadcast |
        ProtocolCapabilities.Recording;

    public SettingsSchema Settings { get; } = BuildSchema();

    /// <summary>Creates a new, not-yet-connected <see cref="LocalShellSession"/> bound to the given context.</summary>
    public IProtocolSession CreateSession(SessionContext ctx) =>
        new LocalShellSession(ctx, _loggerFactory.CreateLogger<LocalShellSession>());

    private static SettingsSchema BuildSchema() => new()
    {
        Fields =
        [
            new() { Key = "shell",      DisplayName = "Shell executable", Type = SettingsFieldType.Text,  Group = "Shell", Description = "e.g. powershell.exe, /bin/bash" },
            new() { Key = "workingDir", DisplayName = "Working directory", Type = SettingsFieldType.FilePath, Group = "Shell" },
        ]
    };
}
