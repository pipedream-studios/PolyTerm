namespace PolyTerm.Engine;

/// <summary>
/// Owns the lifetime of every open <see cref="IProtocolSession"/>: resolves the profile (with folder
/// inheritance) and its credentials, selects the protocol from the registry, and tracks active sessions
/// so the App only ever holds sessions by interface.
/// </summary>
public interface ISessionManager
{
    /// <summary>Sessions currently tracked (not yet <see cref="SessionState.Closed"/> or <see cref="SessionState.Faulted"/>).</summary>
    IReadOnlyCollection<IProtocolSession> ActiveSessions { get; }

    /// <summary>Fires once a session has connected successfully.</summary>
    event EventHandler<SessionEventArgs> SessionOpened;

    /// <summary>Fires when a session reaches <see cref="SessionState.Closed"/> or <see cref="SessionState.Faulted"/>.</summary>
    event EventHandler<SessionEventArgs> SessionClosed;

    /// <summary>Resolves the saved profile by id (applying folder inheritance) and connects it.</summary>
    Task<IProtocolSession> OpenAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Opens a session from a transient (unsaved) profile. Used for local shell tabs
    /// and future quick-connect flows where no vault entry exists.
    /// </summary>
    Task<IProtocolSession> OpenDirectAsync(ConnectionProfile profile, CancellationToken ct = default);

    /// <summary>Gracefully disconnects the session with the given id, if still tracked.</summary>
    Task CloseAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Sends the same input bytes to every listed session that has a <see cref="IProtocolSession.Terminal"/> — the multi-exec/broadcast primitive.</summary>
    Task BroadcastInputAsync(ReadOnlyMemory<byte> data, IEnumerable<Guid> sessionIds, CancellationToken ct = default);
}
