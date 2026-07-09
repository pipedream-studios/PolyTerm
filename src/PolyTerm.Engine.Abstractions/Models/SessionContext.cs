namespace PolyTerm.Engine;

/// <summary>
/// Everything the engine passes to a protocol module when creating a session.
/// The protocol never touches the profile or credential provider directly.
/// </summary>
public sealed class SessionContext
{
    public required Guid SessionId { get; init; }
    public required ConnectionProfile Profile { get; init; }

    /// <summary>
    /// Resolved credential. Caller (SessionManager) owns lifetime; protocol must not cache it.
    /// </summary>
    public CredentialMaterial? Credential { get; init; }

    public required IHostKeyPolicy HostKeyPolicy { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public CancellationToken ApplicationStopping { get; init; }

    /// <summary>
    /// Resolved jump-host chain, ordered outermost-bastion-first (connection order).
    /// Empty when the profile has no jump host configured. Caller (SessionManager) owns
    /// each hop's credential lifetime; protocol must not cache it.
    /// </summary>
    public IReadOnlyList<JumpHopContext> JumpChain { get; init; } = [];

    /// <summary>
    /// Resolves fresh credential material for a reconnect attempt. The original
    /// <see cref="Credential"/>/<see cref="JumpChain"/> are single-use and zeroed shortly
    /// after the initial connect, so a protocol that supports <see cref="ProtocolCapabilities.Reconnect"/>
    /// must call this again for each attempt rather than reusing them. The caller of this
    /// delegate owns disposing the returned material.
    /// </summary>
    public Func<CancellationToken, Task<ReconnectCredentials>>? RefreshCredentialsAsync { get; init; }
}

/// <summary>A single resolved hop in a jump-host chain.</summary>
public sealed class JumpHopContext
{
    public required ConnectionProfile Profile { get; init; }
    public CredentialMaterial? Credential { get; init; }
}

/// <summary>Freshly resolved credential material for a single reconnect attempt.</summary>
public sealed class ReconnectCredentials
{
    public CredentialMaterial? Credential { get; init; }
    public IReadOnlyList<JumpHopContext> JumpChain { get; init; } = [];
}
