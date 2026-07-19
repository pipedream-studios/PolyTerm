using System.Text;

namespace PolyTerm.Engine.Credentials;

/// <summary>
/// Credential provider backed directly by a Bitwarden/Vaultwarden server's own API — no external
/// CLI dependency (issue #78's follow-up: the earlier <c>bw serve</c>-backed version required the
/// <c>bw</c> executable to be installed and on PATH, which this replaces entirely). All actual login
/// and decryption happens in <see cref="BitwardenApiClient"/>/<see cref="BitwardenCrypto"/>; this
/// class only maps that session's decrypted data into the <see cref="ICredentialProvider"/> shape.
/// Only Login items (type 1) appear in BrowseAsync. Existing saved <see cref="CredentialReference"/>
/// values with <c>ProviderId == "bitwarden"</c> keep resolving unchanged — cipher IDs are stable
/// server-side regardless of which client reads them.
/// </summary>
internal sealed class BitwardenProvider : ICredentialProvider
{
    public const string Id = "bitwarden";
    private const int LoginCipherType = 1;

    private readonly BitwardenApiClient _client;

    public BitwardenProvider(BitwardenApiClient client) => _client = client;

    /// <summary>Provider identifier used in <see cref="CredentialReference.ProviderId"/> values (<c>"bitwarden"</c>).</summary>
    public string ProviderId => Id;

    /// <summary>User-facing name shown when selecting this provider.</summary>
    public string DisplayName => "Bitwarden";

    /// <summary>True only while <see cref="BitwardenApiClient"/> currently holds a decrypted session key (logged in and unlocked this run).</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(_client.CheckStatus() == BitwardenAuthStatus.Unlocked);

    /// <summary>Lists Login items from the vault, decrypting each one's name/username with the current session's key(s). Returns an empty list if not logged in. Items belonging to an organization whose key can't be resolved are skipped.</summary>
    public async Task<IReadOnlyList<CredentialSummary>> BrowseAsync(CancellationToken ct = default)
    {
        if (_client.CheckStatus() != BitwardenAuthStatus.Unlocked) return [];

        var sync = await _client.SyncAsync(ct).ConfigureAwait(false);
        var summaries = new List<CredentialSummary>();

        foreach (var cipher in sync.Ciphers)
        {
            if (cipher.Type != LoginCipherType) continue;

            var name = DecryptField(cipher.Name, cipher.OrganizationId, sync, out var ok);
            if (!ok) continue;
            var username = DecryptField(cipher.Username, cipher.OrganizationId, sync, out _);

            summaries.Add(new CredentialSummary
            {
                ProviderId  = Id,
                ItemRef     = cipher.Id,
                DisplayName = name ?? cipher.Id,
                Username    = username,
            });
        }

        return summaries;
    }

    /// <summary>Fetches the item by <see cref="CredentialReference.ItemRef"/> and returns its decrypted username/password.</summary>
    /// <exception cref="InvalidOperationException">Not currently logged in, the item doesn't exist, it has no password to supply, or it belongs to an organization whose key isn't available.</exception>
    public async Task<CredentialMaterial> ResolveAsync(CredentialReference reference, CancellationToken ct = default)
    {
        if (_client.CheckStatus() != BitwardenAuthStatus.Unlocked)
            throw new InvalidOperationException("Bitwarden vault is not unlocked.");

        var sync = await _client.SyncAsync(ct).ConfigureAwait(false);
        var cipher = sync.Ciphers.FirstOrDefault(c => c.Id == reference.ItemRef)
            ?? throw new InvalidOperationException($"Bitwarden item '{reference.ItemRef}' not found.");

        if (cipher.Password is null)
            throw new InvalidOperationException($"Bitwarden item '{reference.ItemRef}' has no password to supply.");

        var password = DecryptField(cipher.Password, cipher.OrganizationId, sync, out var ok);
        if (!ok)
            throw new InvalidOperationException($"Bitwarden item '{reference.ItemRef}' belongs to an organization whose key isn't available.");
        var username = DecryptField(cipher.Username, cipher.OrganizationId, sync, out _) ?? "";

        return new CredentialMaterial
        {
            Username = username,
            Secret   = Encoding.UTF8.GetBytes(password!),
        };
    }

    /// <summary>
    /// Decrypts one EncString field with the right key for its cipher: the user's own key for
    /// personal items (<paramref name="organizationId"/> null), or that organization's key,
    /// unwrapped via <see cref="BitwardenApiClient.TryGetOrganizationKey"/>, for shared ones.
    /// <paramref name="ok"/> is false only when a *required* key couldn't be resolved (an org item
    /// whose org key isn't available); a null <paramref name="encString"/> is a normal "field not
    /// set" case and still reports <paramref name="ok"/> true with a null result.
    /// </summary>
    private string? DecryptField(string? encString, string? organizationId, BitwardenSyncData sync, out bool ok)
    {
        ok = true;
        if (encString is null) return null;

        if (organizationId is null)
            return _client.DecryptWithUserKey(encString);

        if (!_client.TryGetOrganizationKey(sync, organizationId, out var orgKey))
        {
            ok = false;
            return null;
        }
        return BitwardenCrypto.DecryptSymmetricString(BitwardenEncString.Parse(encString), orgKey);
    }
}

/// <summary>Options for connecting to a Bitwarden/Vaultwarden server directly (issue #78).</summary>
public sealed class BitwardenProviderOptions
{
    /// <summary>Base URL of the vault server — the real Bitwarden cloud vault, or a self-hosted Vaultwarden instance.</summary>
    public string BaseUrl { get; set; } = "https://vault.bitwarden.com";

    /// <summary>
    /// A stable per-installation device identifier (a GUID), sent on every login so the server
    /// doesn't treat each PolyTerm launch as a brand-new device. Persisted once in AppSettings.
    /// </summary>
    public string DeviceIdentifier { get; set; } = Guid.NewGuid().ToString();
}
