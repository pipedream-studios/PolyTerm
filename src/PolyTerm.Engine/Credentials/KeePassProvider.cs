using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.Credentials;

/// <summary>
/// Credential provider that talks to a running KeePass instance via KeePassRPC's JSON-RPC interface.
/// PolyTerm never reads .kdbx directly — if KeePass is locked, IsAvailableAsync returns false.
/// </summary>
internal sealed class KeePassProvider : ICredentialProvider, IDisposable
{
    public const string Id = "keepass";

    private readonly KeePassProviderOptions _options;
    private readonly ILogger<KeePassProvider> _logger;
    private readonly HttpClient _http;

    public KeePassProvider(KeePassProviderOptions options, ILogger<KeePassProvider> logger)
    {
        _options = options;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{options.KeePassRpcPort}/") };
    }

    /// <summary>Provider identifier used in <see cref="CredentialReference.ProviderId"/> values (<c>"keepass"</c>).</summary>
    public string ProviderId => Id;

    /// <summary>User-facing name shown when selecting this provider.</summary>
    public string DisplayName => "KeePass (KeePassRPC)";

    /// <summary>Pings the local KeePassRPC endpoint; returns false if KeePass is closed, locked, or unreachable.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("ping", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            _logger.LogDebug("KeePass not reachable on port {Port}.", _options.KeePassRpcPort);
            return false;
        }
    }

    /// <summary>
    /// Lists all entries from the connected KeePass database. Returns an empty list (rather than
    /// throwing) if the request fails; the failure is logged as a warning.
    /// </summary>
    public async Task<IReadOnlyList<CredentialSummary>> BrowseAsync(CancellationToken ct = default)
    {
        try
        {
            var entries = await _http.GetFromJsonAsync<KeePassEntryList>("entries", ct).ConfigureAwait(false);
            return entries?.Entries.Select(e => new CredentialSummary
            {
                ProviderId  = Id,
                ItemRef     = e.Uuid,
                DisplayName = e.Title,
                Username    = e.UserName,
            }).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to browse KeePass entries.");
            return [];
        }
    }

    /// <summary>Fetches the entry by <see cref="CredentialReference.ItemRef"/> (UUID) and returns its username/password.</summary>
    /// <exception cref="InvalidOperationException">KeePassRPC returned an empty response.</exception>
    public async Task<CredentialMaterial> ResolveAsync(CredentialReference reference, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"entry/{Uri.EscapeDataString(reference.ItemRef)}", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var entry = await resp.Content.ReadFromJsonAsync<KeePassEntry>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Null response from KeePassRPC.");

        return new CredentialMaterial
        {
            Username   = entry.UserName,
            Secret     = Encoding.UTF8.GetBytes(entry.Password),
        };
    }

    /// <summary>Disposes the underlying <see cref="HttpClient"/> used to talk to KeePassRPC.</summary>
    public void Dispose() => _http.Dispose();

    private sealed record KeePassEntryList(List<KeePassEntry> Entries);
    private sealed record KeePassEntry(string Uuid, string Title, string UserName, string Password);
}

/// <summary>Options for connecting to a running KeePassRPC instance.</summary>
public sealed class KeePassProviderOptions
{
    /// <summary>Local TCP port that KeePassRPC's JSON-RPC endpoint listens on.</summary>
    public int KeePassRpcPort { get; set; } = 19455; // KeePassRPC default
}
