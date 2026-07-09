using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.Credentials;

/// <summary>
/// Credential provider that talks to the Bitwarden CLI daemon started with <c>bw serve</c>.
/// The vault must be unlocked — IsAvailableAsync returns false if locked or unreachable.
/// Only Login items (type 1) appear in BrowseAsync.
/// </summary>
internal sealed class BitwardenProvider : ICredentialProvider, IDisposable
{
    public const string Id = "bitwarden";

    private readonly BitwardenProviderOptions _options;
    private readonly ILogger<BitwardenProvider> _logger;
    private readonly HttpClient _http;

    public BitwardenProvider(BitwardenProviderOptions options, ILogger<BitwardenProvider> logger)
    {
        _options = options;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };
    }

    /// <summary>Provider identifier used in <see cref="CredentialReference.ProviderId"/> values (<c>"bitwarden"</c>).</summary>
    public string ProviderId => Id;

    /// <summary>User-facing name shown when selecting this provider.</summary>
    public string DisplayName => "Bitwarden";

    /// <summary>Checks the <c>bw serve</c> daemon's status endpoint and returns true only if the vault is unlocked.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("status", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            // data.template.status must be "unlocked" — any other state (locked, unauthenticated) is unusable
            return doc.RootElement.TryGetProperty("data",     out var data)
                && data.TryGetProperty("template", out var tmpl)
                && tmpl.TryGetProperty("status",   out var status)
                && status.GetString() == "unlocked";
        }
        catch
        {
            _logger.LogDebug("Bitwarden not reachable at {Url}.", _options.BaseUrl);
            return false;
        }
    }

    /// <summary>
    /// Lists Login items from the unlocked vault. Returns an empty list (rather than throwing)
    /// if the vault is unreachable or the request fails; the failure is logged as a warning.
    /// </summary>
    public async Task<IReadOnlyList<CredentialSummary>> BrowseAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("list/object/items", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("data", out var data)) return [];
            if (!data.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array) return [];

            var summaries = new List<CredentialSummary>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeProp) || typeProp.GetInt32() != 1) continue;

                var itemId = item.TryGetProperty("id",   out var idProp)   ? idProp.GetString()   : null;
                var name   = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                string? username = null;
                if (item.TryGetProperty("login", out var login))
                    username = login.TryGetProperty("username", out var uProp) ? uProp.GetString() : null;

                if (itemId is null) continue;

                summaries.Add(new CredentialSummary
                {
                    ProviderId  = Id,
                    ItemRef     = itemId,
                    DisplayName = name ?? itemId,
                    Username    = username,
                });
            }
            return summaries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to browse Bitwarden vault.");
            return [];
        }
    }

    /// <summary>
    /// Fetches the item by <see cref="CredentialReference.ItemRef"/> and returns its username/password.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The response is malformed, or the item is not a Login type and has no password to supply.
    /// </exception>
    public async Task<CredentialMaterial> ResolveAsync(CredentialReference reference, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"object/item/{Uri.EscapeDataString(reference.ItemRef)}", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("data", out var data))
            throw new InvalidOperationException("Unexpected Bitwarden response: missing 'data'.");
        if (!data.TryGetProperty("login", out var login))
            throw new InvalidOperationException(
                $"Bitwarden item '{reference.ItemRef}' is not a Login type and cannot supply a password.");

        var username = login.TryGetProperty("username", out var uProp) ? uProp.GetString() ?? "" : "";
        var password = login.TryGetProperty("password", out var pProp) ? pProp.GetString() ?? "" : "";

        return new CredentialMaterial
        {
            Username = username,
            Secret   = Encoding.UTF8.GetBytes(password),
        };
    }

    /// <summary>Disposes the underlying <see cref="HttpClient"/> used to talk to the <c>bw serve</c> daemon.</summary>
    public void Dispose() => _http.Dispose();
}

/// <summary>Options for connecting to a running <c>bw serve</c> daemon.</summary>
public sealed class BitwardenProviderOptions
{
    /// <summary>Base URL of the <c>bw serve</c> HTTP daemon (default: http://localhost:8087).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8087";
}
