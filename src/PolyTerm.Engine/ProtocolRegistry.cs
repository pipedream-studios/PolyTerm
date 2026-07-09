namespace PolyTerm.Engine;

/// <summary>
/// Lookup table of all registered <see cref="IProtocolDescriptor"/> instances, keyed by protocol ID.
/// Descriptors are supplied via DI (each protocol module registers its own) and collected once at
/// construction; the registry does not change after that.
/// </summary>
internal sealed class ProtocolRegistry : IProtocolRegistry
{
    private readonly Dictionary<string, IProtocolDescriptor> _map;

    /// <summary>Builds the lookup from all descriptors registered in the DI container.</summary>
    public ProtocolRegistry(IEnumerable<IProtocolDescriptor> descriptors)
    {
        _map = descriptors.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All registered protocol descriptors, in no particular order.</summary>
    public IReadOnlyList<IProtocolDescriptor> All => [.. _map.Values];

    /// <summary>Looks up a descriptor by its protocol ID (case-insensitive), or null if none is registered.</summary>
    public IProtocolDescriptor? Find(string protocolId) =>
        _map.TryGetValue(protocolId, out var d) ? d : null;
}
