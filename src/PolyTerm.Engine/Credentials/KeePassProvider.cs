using System.Text;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine.Storage;

namespace PolyTerm.Engine.Credentials;

/// <summary>
/// Credential provider that talks to a running KeePass2+KeePassRPC instance over its real WebSocket
/// protocol (<see cref="KeePassRpcClient"/>) — SRP-6 pairing (via <see cref="KeePassPairingStore"/>'s
/// persisted shared key) then encrypted JSON-RPC. PolyTerm never reads .kdbx directly, and never sees a
/// vault master password — only entries a user has already unlocked in KeePass itself and PolyTerm has
/// been paired to see. Returns unavailable/empty everywhere unpaired rather than throwing, except
/// <see cref="ResolveAsync"/> which throws (matching this repo's other providers' convention that
/// resolving a specific, already-selected credential is expected to succeed).
/// </summary>
internal sealed class KeePassProvider : ICredentialProvider
{
    public const string Id = "keepass";

    private readonly KeePassProviderOptions _options;
    private readonly KeePassPairingStore _pairingStore;
    private readonly ILogger<KeePassProvider> _logger;
    private readonly ILogger<KeePassRpcClient> _clientLogger;

    public KeePassProvider(KeePassProviderOptions options, KeePassPairingStore pairingStore,
        ILogger<KeePassProvider> logger, ILogger<KeePassRpcClient> clientLogger)
    {
        _options = options;
        _pairingStore = pairingStore;
        _logger = logger;
        _clientLogger = clientLogger;
    }

    /// <summary>Provider identifier used in <see cref="CredentialReference.ProviderId"/> values (<c>"keepass"</c>).</summary>
    public string ProviderId => Id;

    /// <summary>User-facing name shown when selecting this provider.</summary>
    public string DisplayName => "KeePass (KeePassRPC)";

    /// <summary>False if never paired, KeePass isn't running, or re-authentication with the persisted pairing fails (e.g. the pairing was revoked in KeePass).</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var pairing = await _pairingStore.FindAsync(ct).ConfigureAwait(false);
        if (pairing is null) return false;

        try
        {
            await using var client = CreateClient();
            await client.ConnectAsync(ct).ConfigureAwait(false);
            return await client.AuthenticateAsync(pairing.ClientId, pairing.SharedKeyHex, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "KeePass not reachable on port {Port}.", _options.KeePassRpcPort);
            return false;
        }
    }

    /// <summary>
    /// Lists every entry from the paired KeePass database (recursively walking every group — KeePassRPC
    /// has no single "list everything" call). Returns an empty list (rather than throwing) if unpaired
    /// or the request fails; failures are logged as a warning.
    /// </summary>
    public async Task<IReadOnlyList<CredentialSummary>> BrowseAsync(CancellationToken ct = default)
    {
        try
        {
            var pairing = await _pairingStore.FindAsync(ct).ConfigureAwait(false);
            if (pairing is null) return [];

            await using var client = CreateClient();
            await client.ConnectAsync(ct).ConfigureAwait(false);
            if (!await client.AuthenticateAsync(pairing.ClientId, pairing.SharedKeyHex, ct).ConfigureAwait(false))
                return [];

            var entries = await client.BrowseAllEntriesAsync(pairing.SharedKeyHex, ct).ConfigureAwait(false);
            return entries.Select(e => new CredentialSummary
            {
                ProviderId  = Id,
                ItemRef     = e.Uuid,
                DisplayName = e.Title,
                Username    = e.Username,
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to browse KeePass entries.");
            return [];
        }
    }

    /// <summary>Fetches the entry by <see cref="CredentialReference.ItemRef"/> (UUID) and returns its username/password.</summary>
    /// <exception cref="InvalidOperationException">KeePass isn't paired, authentication failed, or the entry wasn't found.</exception>
    public async Task<CredentialMaterial> ResolveAsync(CredentialReference reference, CancellationToken ct = default)
    {
        var pairing = await _pairingStore.FindAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("KeePass is not paired — pair with KeePass in Vault Settings first.");

        await using var client = CreateClient();
        await client.ConnectAsync(ct).ConfigureAwait(false);
        if (!await client.AuthenticateAsync(pairing.ClientId, pairing.SharedKeyHex, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Could not authenticate with KeePass — the pairing may have been revoked in KeePass itself.");

        var entry = await client.ResolveEntryAsync(pairing.SharedKeyHex, reference.ItemRef, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"KeePass entry '{reference.ItemRef}' was not found.");

        return new CredentialMaterial
        {
            Username = entry.Username,
            Secret   = entry.Password is { Length: > 0 } password ? Encoding.UTF8.GetBytes(password) : null,
        };
    }

    private KeePassRpcClient CreateClient() => new(_options.KeePassRpcPort, _clientLogger);
}

/// <summary>Options for connecting to a running KeePassRPC instance.</summary>
public sealed class KeePassProviderOptions
{
    /// <summary>Local WebSocket port that KeePassRPC listens on.</summary>
    public int KeePassRpcPort { get; set; } = 12546; // KeePassRPC's real default (the old 19455 was never actually KeePassRPC's port)
}
