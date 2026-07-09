namespace PolyTerm.Engine;

/// <summary>Lookup of every registered <see cref="IProtocolDescriptor"/>, keyed by protocol id.</summary>
public interface IProtocolRegistry
{
    /// <summary>Every protocol descriptor registered at startup.</summary>
    IReadOnlyList<IProtocolDescriptor> All { get; }

    /// <summary>Finds the descriptor for a protocol id (e.g. "ssh"), or null if none is registered.</summary>
    IProtocolDescriptor? Find(string protocolId);
}
