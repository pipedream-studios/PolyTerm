using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyTerm.Engine;

/// <summary>
/// A saved SSH port-forward configuration, persisted as part of a profile's
/// <see cref="ConnectionProfile.Settings"/> (see <see cref="TunnelSettingsExtensions"/>). Distinct
/// from the runtime <see cref="TunnelInfo"/> the tunnel manager hands back when a tunnel is actually
/// opened — this type is configuration identity, <see cref="TunnelInfo.Id"/> is instance identity.
/// </summary>
public sealed class TunnelDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required TunnelKind Kind { get; set; }
    public string? Name { get; set; }

    /// <summary>Local bind address for Local/Dynamic forwards, or the remote bind address for Remote forwards. Defaults to loopback-only.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";
    public required int BindPort { get; set; }

    /// <summary>Destination host for Local/Remote forwards. Unused for Dynamic (SOCKS) forwards.</summary>
    public string? DestinationHost { get; set; }

    /// <summary>Destination port for Local/Remote forwards. Unused for Dynamic (SOCKS) forwards.</summary>
    public int? DestinationPort { get; set; }

    /// <summary>Whether this tunnel should be opened automatically when the session connects.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Human-readable one-line description for the tunnel list editor.</summary>
    public string Summary => Kind switch
    {
        TunnelKind.Local   => $"Local  {BindAddress}:{BindPort} → {DestinationHost}:{DestinationPort}",
        TunnelKind.Remote  => $"Remote  {BindAddress}:{BindPort} → {DestinationHost}:{DestinationPort}",
        TunnelKind.Dynamic => $"Dynamic (SOCKS)  {BindAddress}:{BindPort}",
        _ => Kind.ToString(),
    };
}

/// <summary>Reads/writes the list of <see cref="TunnelDefinition"/>s stored under a profile's <c>Settings["tunnels"]</c> key.</summary>
public static class TunnelSettingsExtensions
{
    private const string SettingsKey = "tunnels";
    private static readonly JsonSerializerOptions Opts = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Returns the profile's configured tunnels, or an empty list if none are set.</summary>
    public static List<TunnelDefinition> GetTunnels(this Dictionary<string, JsonElement> settings) =>
        settings.TryGetValue(SettingsKey, out var v)
            ? JsonSerializer.Deserialize<List<TunnelDefinition>>(v, Opts) ?? []
            : [];

    /// <summary>Stores the given tunnels, removing the settings key entirely when the list is empty.</summary>
    public static void SetTunnels(this Dictionary<string, JsonElement> settings, IReadOnlyList<TunnelDefinition> tunnels)
    {
        if (tunnels.Count == 0)
        {
            settings.Remove(SettingsKey);
            return;
        }
        settings[SettingsKey] = JsonSerializer.SerializeToElement(tunnels, Opts);
    }
}
