using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Ssh;

/// <summary>
/// Manages local/remote/dynamic SSH port forwarding over a connected <see cref="SshClient"/>, using SSH.NET's
/// <see cref="ForwardedPortLocal"/>/<see cref="ForwardedPortRemote"/>/<see cref="ForwardedPortDynamic"/>. Each
/// open tunnel is tracked under a fresh <c>Guid</c> assigned at open time (<see cref="TunnelInfo.Id"/>) — this is
/// runtime instance identity and is intentionally distinct from any persisted <c>TunnelDefinition.Id</c> used
/// for saved configuration.
/// </summary>
internal sealed class SshTunnelManager : ITunnelManager, IAsyncDisposable
{
    private readonly SshClient _client;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, (ForwardedPort Port, TunnelInfo Info)> _tunnels = new();

    public SshTunnelManager(SshClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public IReadOnlyList<TunnelInfo> ActiveTunnels => _tunnels.Values.Select(t => t.Info).ToList();

    /// <summary>Opens a local forward: connections to a local port are relayed to a remote host:port.</summary>
    public Task<TunnelInfo> OpenLocalAsync(int localPort, string remoteHost, int remotePort, CancellationToken ct = default) =>
        OpenAsync(new TunnelDefinition
        {
            Kind = TunnelKind.Local,
            BindAddress = "127.0.0.1",
            BindPort = localPort,
            DestinationHost = remoteHost,
            DestinationPort = remotePort,
        }, ct);

    /// <summary>Opens a remote forward: connections to a port on the remote host are relayed to a local host:port.</summary>
    public Task<TunnelInfo> OpenRemoteAsync(int remotePort, string localHost, int localPort, CancellationToken ct = default) =>
        OpenAsync(new TunnelDefinition
        {
            Kind = TunnelKind.Remote,
            BindAddress = "127.0.0.1",
            BindPort = remotePort,
            DestinationHost = localHost,
            DestinationPort = localPort,
        }, ct);

    /// <summary>Opens a dynamic forward (SOCKS proxy) listening on a local port.</summary>
    public Task<TunnelInfo> OpenDynamicAsync(int localPort, CancellationToken ct = default) =>
        OpenAsync(new TunnelDefinition
        {
            Kind = TunnelKind.Dynamic,
            BindAddress = "127.0.0.1",
            BindPort = localPort,
        }, ct);

    /// <summary>Stops and removes a tunnel by its runtime <see cref="TunnelInfo.Id"/>. No-op if the id is not currently open.</summary>
    public Task CloseAsync(Guid tunnelId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (!_tunnels.TryRemove(tunnelId, out var entry))
                return;

            try
            {
                entry.Port.Stop();
                entry.Port.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping tunnel {Id}.", tunnelId);
            }
        }, ct);

    /// <summary>
    /// Opens a tunnel from a saved <see cref="TunnelDefinition"/> (used for the profile's auto-start tunnels
    /// as well as the public Open*Async overloads above). Assigns a fresh runtime <see cref="TunnelInfo.Id"/>
    /// distinct from <paramref name="def"/>'s own identity.
    /// </summary>
    internal Task<TunnelInfo> OpenAsync(TunnelDefinition def, CancellationToken ct = default) => Task.Run(() =>
    {
        ForwardedPort port = def.Kind switch
        {
            TunnelKind.Local => new ForwardedPortLocal(def.BindAddress, (uint)def.BindPort,
                def.DestinationHost!, (uint)def.DestinationPort!.Value),
            TunnelKind.Remote => new ForwardedPortRemote(def.BindAddress, (uint)def.BindPort,
                def.DestinationHost!, (uint)def.DestinationPort!.Value),
            TunnelKind.Dynamic => new ForwardedPortDynamic(def.BindAddress, (uint)def.BindPort),
            _ => throw new NotSupportedException($"Unknown tunnel kind {def.Kind}"),
        };

        var id = Guid.NewGuid();
        port.Exception += (_, e) =>
            _logger.LogWarning(e.Exception, "Tunnel {Id} ({Kind}) reported a runtime error.", id, def.Kind);

        _client.AddForwardedPort(port);
        port.Start();

        var info = new TunnelInfo
        {
            Id = id,
            Kind = def.Kind,
            LocalPort = def.BindPort,
            RemoteHost = def.DestinationHost,
            RemotePort = def.DestinationPort,
        };
        _tunnels[id] = (port, info);
        return info;
    }, ct);

    /// <summary>Closes all currently open tunnels.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var id in _tunnels.Keys.ToList())
            await CloseAsync(id).ConfigureAwait(false);
    }
}
