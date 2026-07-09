namespace PolyTerm.Engine;

/// <summary>Persists known host keys for trust-on-first-use (TOFU) verification across sessions.</summary>
public interface IHostKeyStore
{
    /// <summary>Looks up the previously trusted key for a host:port, if any.</summary>
    Task<HostKeyRecord?> FindAsync(string host, int port, string algorithm, CancellationToken ct = default);

    /// <summary>Records a host key as trusted, inserting a new record or updating the existing one's <see cref="HostKeyRecord.LastSeenAt"/>.</summary>
    Task TrustAsync(HostKeyInfo key, CancellationToken ct = default);

    /// <summary>Lists every trusted host key record, for the host-key management UI.</summary>
    Task<IReadOnlyList<HostKeyRecord>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Forgets a trusted key, e.g. after the user confirms a legitimate key rotation.</summary>
    Task RemoveAsync(string host, int port, CancellationToken ct = default);
}

/// <summary>A single trusted host key record, as persisted by <see cref="IHostKeyStore"/>.</summary>
public sealed class HostKeyRecord
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Algorithm { get; init; }
    public required string FingerprintSha256 { get; init; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
}
