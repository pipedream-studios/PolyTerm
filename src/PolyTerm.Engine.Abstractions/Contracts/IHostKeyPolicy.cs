namespace PolyTerm.Engine;

/// <summary>Decides whether to trust a host key presented during connection (e.g. trust-on-first-use).</summary>
public interface IHostKeyPolicy
{
    /// <summary>
    /// Called before authentication begins. Throw <see cref="HostKeyRejectedException"/>
    /// to abort the connection if the host key should not be trusted.
    /// </summary>
    Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default);
}

/// <summary>A host key as presented by the server during connection, plus what the host-key store already knows about it.</summary>
public sealed class HostKeyInfo
{
    public required string Host { get; init; }
    public required int Port { get; init; }

    /// <summary>Key algorithm name as reported by the protocol library, e.g. "ssh-ed25519".</summary>
    public required string Algorithm { get; init; }

    /// <summary>Base64-encoded SHA-256 fingerprint of the key.</summary>
    public required string FingerprintSha256 { get; init; }
    public required byte[] KeyBytes { get; init; }

    /// <summary>True if no record for this host:port exists in the store yet.</summary>
    public bool IsFirstSeen { get; init; }

    /// <summary>True if a record exists but its fingerprint differs from this presentation — a possible MITM signal.</summary>
    public bool HasChanged { get; init; }
    public DateTimeOffset? FirstSeenAt { get; init; }
}

/// <summary>Outcome of <see cref="IHostKeyPolicy.EvaluateAsync"/>.</summary>
public enum HostKeyDecision
{
    /// <summary>Proceed with authentication.</summary>
    Trust,
    /// <summary>Abort the connection.</summary>
    Reject,
    /// <summary>Ask the user before deciding (policy implementations that support interactive prompts).</summary>
    Prompt,
}

/// <summary>Thrown by an <see cref="IHostKeyPolicy"/> implementation to abort a connection whose host key is not trusted.</summary>
public sealed class HostKeyRejectedException : Exception
{
    public HostKeyRejectedException() : base("Host key rejected.") { }
    public HostKeyRejectedException(string host) : base($"Host key rejected for {host}") { }
    public HostKeyRejectedException(string message, Exception innerException) : base(message, innerException) { }
}
