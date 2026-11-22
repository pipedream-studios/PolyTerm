using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<BitwardenProvider> _logger;

    public BitwardenProvider(BitwardenApiClient client, ILogger<BitwardenProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Provider identifier used in <see cref="CredentialReference.ProviderId"/> values (<c>"bitwarden"</c>).</summary>
    public string ProviderId => Id;

    /// <summary>User-facing name shown when selecting this provider.</summary>
    public string DisplayName => "Bitwarden";

    /// <summary>True only while <see cref="BitwardenApiClient"/> currently holds a decrypted session key (logged in and unlocked this run).</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(_client.CheckStatus() == BitwardenAuthStatus.Unlocked);

    /// <summary>
    /// Lists Login items from the vault, decrypting each one's name/username with the current session's
    /// key(s). Returns an empty list if not logged in. Items belonging to an organization whose key can't
    /// be resolved are skipped, and so is any item that fails to decrypt (logged as a warning), so one
    /// bad item can't hide the rest of the vault (issue #200).
    /// </summary>
    public async Task<IReadOnlyList<CredentialSummary>> BrowseAsync(CancellationToken ct = default)
    {
        if (_client.CheckStatus() != BitwardenAuthStatus.Unlocked) return [];

        var sync = await _client.SyncAsync(ct).ConfigureAwait(false);
        var summaries = new List<CredentialSummary>();

        foreach (var cipher in sync.Ciphers)
        {
            if (cipher.Type != LoginCipherType) continue;

            try
            {
                if (!TryGetItemKey(cipher, sync, out var itemKey)) continue;

                summaries.Add(new CredentialSummary
                {
                    ProviderId  = Id,
                    ItemRef     = cipher.Id,
                    DisplayName = DecryptField(cipher.Name, itemKey) ?? cipher.Id,
                    Username    = DecryptField(cipher.Username, itemKey),
                });
            }
            catch (Exception ex) when (IsDecryptionFailure(ex))
            {
                _logger.LogWarning(ex, "Skipping Bitwarden item {CipherId}: it could not be decrypted.", cipher.Id);
            }
        }

        return summaries;
    }

    /// <summary>Fetches the item by <see cref="CredentialReference.ItemRef"/> and returns its decrypted username/password.</summary>
    /// <exception cref="InvalidOperationException">Not currently logged in, the item doesn't exist, it has no password to supply, it belongs to an organization whose key isn't available, or it couldn't be decrypted.</exception>
    public async Task<CredentialMaterial> ResolveAsync(CredentialReference reference, CancellationToken ct = default)
    {
        if (_client.CheckStatus() != BitwardenAuthStatus.Unlocked)
            throw new InvalidOperationException("Bitwarden vault is not unlocked.");

        var sync = await _client.SyncAsync(ct).ConfigureAwait(false);
        var cipher = sync.Ciphers.FirstOrDefault(c => c.Id == reference.ItemRef)
            ?? throw new InvalidOperationException($"Bitwarden item '{reference.ItemRef}' not found.");

        if (cipher.Password is null)
            throw new InvalidOperationException($"Bitwarden item '{reference.ItemRef}' has no password to supply.");

        try
        {
            if (!TryGetItemKey(cipher, sync, out var itemKey))
                throw new InvalidOperationException($"Bitwarden item '{reference.ItemRef}' belongs to an organization whose key isn't available.");

            return new CredentialMaterial
            {
                Username = DecryptField(cipher.Username, itemKey) ?? "",
                Secret   = Encoding.UTF8.GetBytes(DecryptField(cipher.Password, itemKey)!),
            };
        }
        catch (Exception ex) when (IsDecryptionFailure(ex))
        {
            throw new InvalidOperationException($"Couldn't decrypt Bitwarden item '{reference.ItemRef}'.", ex);
        }
    }

    /// <summary>
    /// Resolves the key that <paramref name="cipher"/>'s fields are encrypted with. The starting point is
    /// the user's own key for personal items, or the organization's key (unwrapped via
    /// <see cref="BitwardenApiClient.TryGetOrganizationKey"/>) for shared ones. If the item carries its own
    /// cipher key (<see cref="BitwardenSyncCipher.Key"/>), that is decrypted with the starting key and
    /// used instead. Returns false only when a required organization key isn't available.
    /// </summary>
    /// <exception cref="CryptographicException">The item's own key couldn't be decrypted, or isn't a 64-byte key.</exception>
    private bool TryGetItemKey(BitwardenSyncCipher cipher, BitwardenSyncData sync, out byte[] key)
    {
        byte[] baseKey;
        if (cipher.OrganizationId is null)
        {
            baseKey = _client.GetUserKey();
        }
        else if (!_client.TryGetOrganizationKey(sync, cipher.OrganizationId, out baseKey))
        {
            key = [];
            return false;
        }

        if (cipher.Key is null)
        {
            key = baseKey;
            return true;
        }

        key = BitwardenCrypto.DecryptSymmetric(BitwardenEncString.Parse(cipher.Key), baseKey);
        if (key.Length != 64)
            throw new CryptographicException($"Bitwarden item key is {key.Length} bytes; expected 64.");
        return true;
    }

    /// <summary>Decrypts one EncString field with the item's key. A null <paramref name="encString"/> means "field not set" and returns null.</summary>
    private static string? DecryptField(string? encString, byte[] itemKey) =>
        encString is null ? null : BitwardenCrypto.DecryptSymmetricString(BitwardenEncString.Parse(encString), itemKey);

    /// <summary>What a corrupt, tampered or unsupported EncString throws from <see cref="BitwardenEncString.Parse"/>/<see cref="BitwardenCrypto"/>.</summary>
    private static bool IsDecryptionFailure(Exception ex) =>
        ex is CryptographicException or FormatException or NotSupportedException;
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
