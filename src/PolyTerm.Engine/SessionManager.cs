using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine;

/// <summary>
/// Owns the lifetime of every open <see cref="IProtocolSession"/>: resolves profiles and their
/// credentials, builds the <see cref="SessionContext"/> handed to protocol modules, tracks
/// sessions in memory keyed by session ID, and removes them automatically when they close or
/// fault. All access to the in-memory session table is synchronized with a single lock.
/// </summary>
internal sealed class SessionManager : ISessionManager
{
    private readonly IProtocolRegistry _registry;
    private readonly IProfileStore _profiles;
    private readonly IEnumerable<ICredentialProvider> _credentialProviders;
    private readonly IHostKeyPolicy _hostKeyPolicy;
    private readonly ILogger<SessionManager> _logger;

    private readonly Dictionary<Guid, IProtocolSession> _sessions = [];
    private readonly object _lock = new();

    public SessionManager(
        IProtocolRegistry registry,
        IProfileStore profiles,
        IEnumerable<ICredentialProvider> credentialProviders,
        IHostKeyPolicy hostKeyPolicy,
        ILogger<SessionManager> logger)
    {
        _registry = registry;
        _profiles = profiles;
        _credentialProviders = credentialProviders;
        _hostKeyPolicy = hostKeyPolicy;
        _logger = logger;
    }

    /// <summary>Snapshot of all sessions currently tracked as open (taken under the internal lock).</summary>
    public IReadOnlyCollection<IProtocolSession> ActiveSessions
    {
        get { lock (_lock) { return [.. _sessions.Values]; } }
    }

    /// <summary>Raised synchronously, on the caller's thread, immediately after a session connects successfully.</summary>
    public event EventHandler<SessionEventArgs>? SessionOpened;

    /// <summary>
    /// Raised synchronously from the protocol session's <c>StateChanged</c> callback when a
    /// tracked session transitions to <see cref="SessionState.Closed"/> or <see cref="SessionState.Faulted"/>.
    /// The session has already been removed from <see cref="ActiveSessions"/> by the time this fires.
    /// </summary>
    public event EventHandler<SessionEventArgs>? SessionClosed;

    /// <summary>
    /// Opens a session for a saved profile, resolving it (with inheritance) from the profile store first.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No profile with <paramref name="profileId"/> exists.</exception>
    /// <exception cref="InvalidOperationException">The profile's protocol ID is not registered, or its jump-host chain contains a cycle.</exception>
    public async Task<IProtocolSession> OpenAsync(Guid profileId, CancellationToken ct = default)
    {
        var profile = await _profiles.ResolveAsync(profileId, ct).ConfigureAwait(false);
        _logger.LogInformation("Opening session for profile {ProfileId} ({Protocol})", profileId, profile.ProtocolId);
        return await OpenCoreAsync(profile, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a session from a transient (unsaved) profile, bypassing the profile store entirely.
    /// Used for local shell tabs and quick-connect flows where no vault entry exists.
    /// </summary>
    public Task<IProtocolSession> OpenDirectAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        _logger.LogInformation("Opening direct session ({Protocol})", profile.ProtocolId);
        return OpenCoreAsync(profile, ct);
    }

    /// <summary>
    /// Resolves the profile's protocol descriptor, credential, and jump-host chain, then creates
    /// and connects the session.
    /// </summary>
    /// <remarks>
    /// Credential lifetime contract: <paramref name="profile"/>'s credential and every jump hop's
    /// credential are resolved exactly once up front, handed to the protocol synchronously via
    /// <see cref="SessionContext"/> for the initial <c>ConnectAsync</c> call, and then always
    /// disposed in the <c>finally</c> block below — regardless of whether the connect succeeded,
    /// failed, or threw. Protocols must not retain references to <see cref="SessionContext.Credential"/>
    /// or <see cref="SessionContext.JumpChain"/> credentials beyond the initial connect.
    /// <para/>
    /// For protocols that support <see cref="ProtocolCapabilities.Reconnect"/>, the context's
    /// <see cref="SessionContext.RefreshCredentialsAsync"/> closure captures <paramref name="profile"/>
    /// and the resolved jump-chain profiles (not their credentials) and re-resolves fresh, single-use
    /// credential material on each reconnect attempt; the delegate's caller owns disposing what it returns.
    /// </remarks>
    private async Task<IProtocolSession> OpenCoreAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var descriptor = _registry.Find(profile.ProtocolId)
            ?? throw new InvalidOperationException($"Unknown protocol '{profile.ProtocolId}'.");

        var credential = await ResolveCredentialAsync(profile, ct).ConfigureAwait(false);
        var chainProfiles = await ResolveJumpChainAsync(profile, ct).ConfigureAwait(false);
        var jumpChain = new List<JumpHopContext>();
        try
        {
            foreach (var hop in chainProfiles)
            {
                var hopCredential = await ResolveCredentialAsync(hop, ct).ConfigureAwait(false);
                jumpChain.Add(new JumpHopContext { Profile = hop, Credential = hopCredential });
            }

            var ctx = new SessionContext
            {
                SessionId     = Guid.NewGuid(),
                Profile       = profile,
                Credential    = credential,
                JumpChain     = jumpChain,
                HostKeyPolicy = _hostKeyPolicy,
                ApplicationStopping = ct,
                RefreshCredentialsAsync = async rct =>
                {
                    var freshCredential = await ResolveCredentialAsync(profile, rct).ConfigureAwait(false);
                    var freshChain = new List<JumpHopContext>();
                    foreach (var hop in chainProfiles)
                    {
                        var hopCred = await ResolveCredentialAsync(hop, rct).ConfigureAwait(false);
                        freshChain.Add(new JumpHopContext { Profile = hop, Credential = hopCred });
                    }
                    return new ReconnectCredentials { Credential = freshCredential, JumpChain = freshChain };
                },
            };

            var session = descriptor.CreateSession(ctx);
            session.StateChanged += OnSessionStateChanged;

            lock (_lock) { _sessions[session.SessionId] = session; }

            try
            {
                await session.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                lock (_lock) { _sessions.Remove(session.SessionId); }
                throw;
            }

            SessionOpened?.Invoke(this, new SessionEventArgs { Session = session });
            return session;
        }
        finally
        {
            credential?.Dispose();
            foreach (var hop in jumpChain)
                hop.Credential?.Dispose();
        }
    }

    private async Task<CredentialMaterial?> ResolveCredentialAsync(ConnectionProfile profile, CancellationToken ct)
    {
        if (profile.Credential is not { } credRef) return null;

        var provider = _credentialProviders.FirstOrDefault(p => p.ProviderId == credRef.ProviderId)
            ?? throw new InvalidOperationException($"No credential provider '{credRef.ProviderId}'.");
        return await provider.ResolveAsync(credRef, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Walks <paramref name="profile"/>'s jump-host references up to the root, returning the
    /// resolved chain ordered outermost-bastion-first (i.e. connection order — the opposite of
    /// the nearest-hop-first reference order stored on each profile).
    /// </summary>
    /// <exception cref="InvalidOperationException">The chain revisits a profile already seen, i.e. a cycle.</exception>
    private async Task<List<ConnectionProfile>> ResolveJumpChainAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var chain = new List<ConnectionProfile>();
        var visited = new HashSet<Guid> { profile.Id };
        var current = profile;

        while (current.Settings.GetJumpHostProfileId() is { } hopId)
        {
            if (!visited.Add(hopId))
                throw new InvalidOperationException("Jump host chain contains a cycle.");

            current = await _profiles.ResolveAsync(hopId, ct).ConfigureAwait(false);
            chain.Add(current);
        }

        chain.Reverse(); // reference order is nearest-hop-first; connection order is outermost-first
        return chain;
    }

    /// <summary>
    /// Gracefully disconnects the session with the given id, if still tracked. No-op if the id is
    /// unknown (e.g. already closed). The session is removed from <see cref="ActiveSessions"/> via
    /// the usual <see cref="OnSessionStateChanged"/> callback once it reaches <see cref="SessionState.Closed"/>.
    /// </summary>
    public async Task CloseAsync(Guid sessionId, CancellationToken ct = default)
    {
        IProtocolSession? session;
        lock (_lock) { _sessions.TryGetValue(sessionId, out session); }

        if (session is null) return;

        await session.DisconnectAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the same input bytes to every listed session that has a <see cref="IProtocolSession.Terminal"/>,
    /// concurrently. Unknown session ids and sessions without a terminal channel are silently skipped.
    /// </summary>
    public async Task BroadcastInputAsync(ReadOnlyMemory<byte> data, IEnumerable<Guid> sessionIds, CancellationToken ct = default)
    {
        IProtocolSession[] targets;
        lock (_lock)
        {
            targets = sessionIds
                .Select(id => _sessions.TryGetValue(id, out var s) ? s : null)
                .OfType<IProtocolSession>()
                .Where(s => s.Terminal is not null)
                .ToArray();
        }

        var tasks = targets.Select(s => s.Terminal!.SendInputAsync(data, ct).AsTask());
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (sender is not IProtocolSession session) return;

        if (e.Current is SessionState.Closed or SessionState.Faulted)
        {
            lock (_lock) { _sessions.Remove(session.SessionId); }
            SessionClosed?.Invoke(this, new SessionEventArgs { Session = session });
            _logger.LogInformation("Session {SessionId} closed (state={State})", session.SessionId, e.Current);
        }
    }
}
