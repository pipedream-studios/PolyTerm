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
/// Only Login items (type 1) and SSH Key items (type 5, issue #283) appear in BrowseAsync. Existing saved <see cref="CredentialReference"/>
/// values with <c>ProviderId == "bitwarden"</c> keep resolving unchanged — cipher IDs are stable
/// server-side regardless of which client reads them.
/// </summary>
internal sealed class BitwardenProvider : ICredentialProvider, ICredentialBrowseNotice
{
    public const string Id = "bitwarden";
    private const int LoginCipherType = 1;
    private const int SshKeyCipherType = 5;
    private const string LogInAgainHint = "To log in again, use Log In / Unlock… in Vault Provider.";

    /// <summary>Separates the levels of <see cref="CredentialSummary.Location"/>: <c>My vault › Servers</c>, <c>Org › collection</c>.</summary>
    private const string LocationSeparator = " \u203A ";
    private const string PersonalVaultName = "My vault";
    private const string UnknownFolder = "(unknown folder)";
    private const string UnknownCollection = "(unknown collection)";
    private const string UnknownOrganization = "(unknown organization)";

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

    /// <summary>Says how many organization items the last <see cref="BrowseAsync"/> skipped for want of a key (issue #384).</summary>
    public string? BrowseNotice { get; private set; }

    /// <summary>
    /// Lists Login and SSH Key items from the vault, decrypting each one's name/username with the current session's
    /// key(s). Returns an empty list if not logged in. Items belonging to an organization whose key can't
    /// be resolved are skipped (and counted in <see cref="BrowseNotice"/>), and so is any item that fails to decrypt (logged as a warning), so one
    /// bad item can't hide the rest of the vault (issue #200). Each entry's <see cref="CredentialSummary.Location"/>
    /// says whether it's personal (and in which folder) or shared (which organization and collections), issue #285.
    /// SSH Key items (issue #283) come back as <see cref="CredentialKind.SshKey"/> with their fingerprint and no
    /// username; one with no <c>sshKey</c> data at all is skipped.
    /// </summary>
    public async Task<IReadOnlyList<CredentialSummary>> BrowseAsync(CancellationToken ct = default)
    {
        BrowseNotice = null;
        if (_client.CheckStatus() != BitwardenAuthStatus.Unlocked) return [];

        var sync = await _client.SyncAsync(ct).ConfigureAwait(false);
        var summaries = new List<CredentialSummary>();
        var withoutOrganizationKey = 0;
        var locations = new LocationResolver(this, sync);

        foreach (var cipher in sync.Ciphers)
        {
            var isSshKey = cipher.Type == SshKeyCipherType;
            if (cipher.Type != LoginCipherType && !(isSshKey && cipher.SshKey is not null)) continue;

            try
            {
                if (!TryGetItemKey(cipher, sync, out var itemKey))
                {
                    withoutOrganizationKey++;
                    continue;
                }

                summaries.Add(new CredentialSummary
                {
                    ProviderId     = Id,
                    ItemRef        = cipher.Id,
                    DisplayName    = DecryptField(cipher.Name, itemKey) ?? cipher.Id,
                    Username       = isSshKey ? null : DecryptField(cipher.Username, itemKey),
                    Location       = locations.Describe(cipher),
                    Kind           = isSshKey ? CredentialKind.SshKey : CredentialKind.Password,
                    KeyFingerprint = isSshKey ? DecryptField(cipher.SshKey!.KeyFingerprint, itemKey) : null,
                });
            }
            catch (Exception ex) when (IsDecryptionFailure(ex))
            {
                _logger.LogWarning(ex, "Skipping Bitwarden item {CipherId}: it could not be decrypted.", cipher.Id);
            }
        }

        if (withoutOrganizationKey > 0)
        {
            var skipped = withoutOrganizationKey == 1 ? "1 organization item isn't" : $"{withoutOrganizationKey} organization items aren't";
            BrowseNotice = $"{skipped} shown, because the organization's key isn't available. {LogInAgainHint}";
        }

        return summaries;
    }

    /// <summary>
    /// Fetches the item by <see cref="CredentialReference.ItemRef"/> and returns its decrypted username/password, or
    /// for an SSH Key item (issue #283) its decrypted private key as <see cref="CredentialMaterial.PrivateKey"/> with no
    /// <see cref="CredentialMaterial.Secret"/> (Bitwarden stores these keys without a passphrase).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not currently logged in, the item doesn't exist, it has no password (or private key) to supply, it belongs to an organization whose key isn't available, or it couldn't be decrypted.</exception>
    public async Task<CredentialMaterial> ResolveAsync(CredentialReference reference, CancellationToken ct = default)
    {
        if (_client.CheckStatus() != BitwardenAuthStatus.Unlocked)
            throw new InvalidOperationException("Bitwarden vault is not unlocked.");

        var sync = await _client.SyncAsync(ct).ConfigureAwait(false);
        var cipher = sync.Ciphers.FirstOrDefault(c => c.Id == reference.ItemRef)
            ?? throw new InvalidOperationException($"Bitwarden item '{reference.ItemRef}' not found.");

        var isSshKey = cipher.Type == SshKeyCipherType;
        if (isSshKey && cipher.SshKey?.PrivateKey is null)
            throw new InvalidOperationException($"Bitwarden item '{reference.ItemRef}' has no private key to supply.");
        if (!isSshKey && cipher.Password is null)
            throw new InvalidOperationException($"Bitwarden item '{reference.ItemRef}' has no password to supply.");

        try
        {
            if (!TryGetItemKey(cipher, sync, out var itemKey))
                throw new InvalidOperationException($"Bitwarden item '{reference.ItemRef}' belongs to an organization whose key isn't available. {LogInAgainHint}");

            if (isSshKey)
            {
                // Decrypt to bytes, not a string, so the only copy of the key is the buffer CredentialMaterial zeroes.
                return new CredentialMaterial
                {
                    PrivateKey = BitwardenCrypto.DecryptSymmetric(BitwardenEncString.Parse(cipher.SshKey!.PrivateKey!), itemKey),
                };
            }

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

    /// <summary>
    /// Builds <see cref="CredentialSummary.Location"/> strings for one sync, decrypting each folder and
    /// collection name at most once however many items share it. A name that can't be decrypted (or a
    /// folder/collection/org the sync doesn't describe) shows as a placeholder rather than hiding the
    /// item, the same approach as issue #200.
    /// </summary>
    private sealed class LocationResolver(BitwardenProvider provider, BitwardenSyncData sync)
    {
        private readonly Dictionary<string, string> _folderNames = new();
        private readonly Dictionary<string, string> _collectionNames = new();

        public string Describe(BitwardenSyncCipher cipher)
        {
            if (cipher.OrganizationId is null)
            {
                return cipher.FolderId is { } folderId
                    ? PersonalVaultName + LocationSeparator + FolderName(folderId)
                    : PersonalVaultName;
            }

            var orgName = sync.Organizations.FirstOrDefault(o => o.Id == cipher.OrganizationId)?.Name;
            var location = string.IsNullOrEmpty(orgName) ? UnknownOrganization : orgName;

            if (cipher.CollectionIds is { Count: > 0 } collectionIds)
            {
                var names = collectionIds
                    .Select(CollectionName)
                    .Distinct()
                    .Order(StringComparer.CurrentCultureIgnoreCase);
                location += LocationSeparator + string.Join(", ", names);
            }

            return location;
        }

        private string FolderName(string folderId)
        {
            if (_folderNames.TryGetValue(folderId, out var cached)) return cached;

            var folder = sync.Folders?.FirstOrDefault(f => f.Id == folderId);
            var name = folder?.Name is { } encName
                ? TryDecryptName(encName, provider._client.GetUserKey(), "folder", folderId) ?? UnknownFolder
                : UnknownFolder;
            return _folderNames[folderId] = name;
        }

        private string CollectionName(string collectionId)
        {
            if (_collectionNames.TryGetValue(collectionId, out var cached)) return cached;

            var collection = sync.Collections?.FirstOrDefault(c => c.Id == collectionId);
            string? name = null;
            if (collection?.Name is { } encName &&
                provider._client.TryGetOrganizationKey(sync, collection.OrganizationId, out var orgKey))
            {
                name = TryDecryptName(encName, orgKey, "collection", collectionId);
            }
            return _collectionNames[collectionId] = name ?? UnknownCollection;
        }

        private string? TryDecryptName(string encName, byte[] key, string kind, string id)
        {
            try
            {
                return DecryptField(encName, key);
            }
            catch (Exception ex) when (IsDecryptionFailure(ex))
            {
                provider._logger.LogWarning(ex, "Could not decrypt the name of Bitwarden {Kind} {Id}.", kind, id);
                return null;
            }
        }
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
