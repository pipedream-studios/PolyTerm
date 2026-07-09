namespace PolyTerm.Engine;

/// <summary>Local/remote/dynamic (SOCKS) port forwarding over an already-connected session. Exposed via <see cref="IProtocolSession.Tunnels"/> when the protocol declares <see cref="ProtocolCapabilities.PortForwarding"/>.</summary>
public interface ITunnelManager
{
    /// <summary>Tunnels currently open on this session.</summary>
    IReadOnlyList<TunnelInfo> ActiveTunnels { get; }

    /// <summary>Opens a local forward: connections to <paramref name="localPort"/> on this machine are relayed to <paramref name="remoteHost"/>:<paramref name="remotePort"/> on the far side.</summary>
    Task<TunnelInfo> OpenLocalAsync(int localPort, string remoteHost, int remotePort, CancellationToken ct = default);

    /// <summary>Opens a remote forward: connections to <paramref name="remotePort"/> on the far side are relayed to <paramref name="localHost"/>:<paramref name="localPort"/> on this machine.</summary>
    Task<TunnelInfo> OpenRemoteAsync(int remotePort, string localHost, int localPort, CancellationToken ct = default);

    /// <summary>Opens a dynamic (SOCKS) forward on <paramref name="localPort"/>, letting a SOCKS-aware client route arbitrary destinations through the session.</summary>
    Task<TunnelInfo> OpenDynamicAsync(int localPort, CancellationToken ct = default);

    /// <summary>Closes a previously opened tunnel by its <see cref="TunnelInfo.Id"/>. A no-op if already closed.</summary>
    Task CloseAsync(Guid tunnelId, CancellationToken ct = default);
}

/// <summary>
/// A currently-open tunnel. <see cref="Id"/> is runtime instance identity, assigned fresh each time
/// a tunnel is opened — distinct from any persisted tunnel-configuration identity (e.g. a saved
/// <c>TunnelDefinition.Id</c>).
/// </summary>
public sealed class TunnelInfo
{
    public required Guid Id { get; init; }
    public required TunnelKind Kind { get; init; }
    public required int LocalPort { get; init; }
    public string? RemoteHost { get; init; }
    public int? RemotePort { get; init; }
}

/// <summary>Direction/style of an SSH port forward.</summary>
public enum TunnelKind
{
    /// <summary>Local machine port → remote destination.</summary>
    Local,
    /// <summary>Remote port → local machine destination.</summary>
    Remote,
    /// <summary>Local SOCKS proxy port with no fixed destination.</summary>
    Dynamic,
}
