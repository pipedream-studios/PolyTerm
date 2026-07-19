namespace PolyTerm.Engine;

/// <summary>
/// Resolves credential references against a backing secret store (OS keychain, KeePass, Bitwarden).
/// PolyTerm does not implement its own vault — every provider defers to a store the user already runs.
/// </summary>
public interface ICredentialProvider
{
    /// <summary>Stable identifier used in <see cref="CredentialReference.ProviderId"/>, e.g. "os-keychain".</summary>
    string ProviderId { get; }

    /// <summary>Human-readable name shown in the credential picker UI.</summary>
    string DisplayName { get; }

    /// <summary>Whether the backing store is currently reachable/unlocked (e.g. is the KeePassRPC plugin running, or the Bitwarden session logged in and unlocked?).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Resolves a reference to its actual secret material. Caller owns disposing the result.</summary>
    Task<CredentialMaterial> ResolveAsync(CredentialReference reference, CancellationToken ct = default);

    /// <summary>Lists available items (metadata only, no secrets) for the credential picker UI.</summary>
    Task<IReadOnlyList<CredentialSummary>> BrowseAsync(CancellationToken ct = default);
}
