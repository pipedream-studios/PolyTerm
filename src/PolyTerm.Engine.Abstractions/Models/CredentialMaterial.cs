namespace PolyTerm.Engine;

/// <summary>Points at a credential in a provider's store without carrying any secret material itself — safe to persist on a <see cref="ConnectionProfile"/>.</summary>
public readonly record struct CredentialReference(string ProviderId, string ItemRef);

/// <summary>Metadata-only listing entry for the credential picker UI — never carries secrets.</summary>
public sealed class CredentialSummary
{
    public required string ProviderId { get; init; }
    public required string ItemRef { get; init; }
    public required string DisplayName { get; init; }
    public string? Username { get; init; }
}

/// <summary>
/// Resolved credential material. Buffers are zeroed on Dispose.
/// Never serialized, never logged, never crosses the MCP boundary.
/// </summary>
public sealed class CredentialMaterial : IDisposable
{
    private bool _disposed;

    public string? Username { get; init; }

    /// <summary>Password or passphrase bytes, UTF-8. Zeroed on Dispose.</summary>
    public byte[]? Secret { get; init; }

    /// <summary>OpenSSH-format private key bytes. Zeroed on Dispose.</summary>
    public byte[]? PrivateKey { get; init; }

    /// <summary>Path to a key file on disk (not read by the credential provider — SSH module resolves it).</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>Zeroes <see cref="Secret"/> and <see cref="PrivateKey"/> in place. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Secret is not null) Array.Clear(Secret);
        if (PrivateKey is not null) Array.Clear(PrivateKey);
    }
}
