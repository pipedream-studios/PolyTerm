namespace PolyTerm.Engine;

/// <summary>
/// A single connected (or connecting) instance of a protocol module — one SSH connection,
/// one local shell, one RDP/VNC desktop, etc. Created by <see cref="IProtocolDescriptor.CreateSession"/>
/// and owned thereafter by <see cref="ISessionManager"/>. The capability accessors
/// (<see cref="Terminal"/>, <see cref="FileTransfer"/>, <see cref="Framebuffer"/>, <see cref="Tunnels"/>, <see cref="NativeHost"/>)
/// return null when the underlying protocol doesn't support that capability — the App should check
/// the accessor, not the protocol id, so it never has to special-case a specific protocol.
/// </summary>
public interface IProtocolSession : IAsyncDisposable
{
    /// <summary>Stable identity for this session instance, assigned when the session was opened.</summary>
    Guid SessionId { get; }

    /// <summary>The resolved profile this session was opened from.</summary>
    ConnectionProfile Profile { get; }

    /// <summary>Current lifecycle state. Changes are announced via <see cref="StateChanged"/>.</summary>
    SessionState State { get; }

    /// <summary>Fires whenever <see cref="State"/> changes. Raised on an engine thread.</summary>
    event EventHandler<SessionStateChangedEventArgs> StateChanged;

    /// <summary>Fires when the session encounters an error. Raised on an engine thread.</summary>
    event EventHandler<SessionErrorEventArgs> ErrorOccurred;

    /// <summary>Establishes the connection. Transitions through <see cref="SessionState.Connecting"/> to
    /// <see cref="SessionState.Connected"/> on success, or <see cref="SessionState.Faulted"/> on failure.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Gracefully tears down the connection, ending in <see cref="SessionState.Closed"/>.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Non-null when the protocol declares <see cref="ProtocolCapabilities.Terminal"/>.</summary>
    ITerminalChannel? Terminal { get; }

    /// <summary>Non-null when the protocol declares <see cref="ProtocolCapabilities.FileTransfer"/>.</summary>
    IFileTransferChannel? FileTransfer { get; }

    /// <summary>Non-null when the protocol declares <see cref="ProtocolCapabilities.GraphicalDesktop"/>.</summary>
    IFramebufferChannel? Framebuffer { get; }

    /// <summary>Non-null when the protocol declares <see cref="ProtocolCapabilities.PortForwarding"/>.</summary>
    ITunnelManager? Tunnels { get; }

    /// <summary>Non-null when the protocol declares <see cref="ProtocolCapabilities.GraphicalDesktop"/> with <see cref="IProtocolDescriptor.Surface"/> of <see cref="SurfaceKind.NativeHost"/>.</summary>
    INativeHostChannel? NativeHost { get; }
}
