namespace PolyTerm.Engine;

/// <summary>Points at a credential in a provider's store without carrying any secret material itself — safe to persist on a <see cref="ConnectionProfile"/>.</summary>
public readonly record struct CredentialReference(string ProviderId, string ItemRef);

/// <summary>What a vault entry supplies when resolved (issue #283).</summary>
public enum CredentialKind
{
    /// <summary>A username + password (<see cref="CredentialMaterial.Secret"/>). Usable by every protocol that authenticates.</summary>
    Password,

    /// <summary>An in-memory SSH private key (<see cref="CredentialMaterial.PrivateKey"/>) and no password. Only SSH connections can use it.</summary>
    SshKey,
}

/// <summary>Metadata-only listing entry for the credential picker UI — never carries secrets.</summary>
public sealed class CredentialSummary
{
    public required string ProviderId { get; init; }
    public required string ItemRef { get; init; }
    public required string DisplayName { get; init; }
    public string? Username { get; init; }

    /// <summary>
    /// Where the entry lives inside the provider's store, for telling similarly named entries apart in
    /// the picker (issue #285) — e.g. <c>My vault › Servers</c> or <c>Automation › infra, health</c> for
    /// Bitwarden; a KeePass group path would fit here too. Null when the provider doesn't say.
    /// </summary>
    public string? Location { get; init; }

    /// <summary>What the entry resolves to (issue #283). Defaults to <see cref="CredentialKind.Password"/>, which is all most providers offer.</summary>
    public CredentialKind Kind { get; init; } = CredentialKind.Password;

    /// <summary>The public key's fingerprint (e.g. <c>SHA256:…</c>) for a <see cref="CredentialKind.SshKey"/> entry, shown in the picker in place of a username. Null otherwise, or when the provider doesn't say.</summary>
    public string? KeyFingerprint { get; init; }

    /// <summary>Convenience for UI bindings: <see cref="Kind"/> is <see cref="CredentialKind.SshKey"/>.</summary>
    public bool IsSshKey => Kind == CredentialKind.SshKey;
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
