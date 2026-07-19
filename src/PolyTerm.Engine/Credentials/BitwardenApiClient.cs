using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.Credentials;

/// <summary>Login/unlock state, tracked purely in-memory for this process's lifetime — nothing persists across restarts, matching the CLI-based version's own <c>bw serve</c>-is-memory-only behavior.</summary>
public enum BitwardenAuthStatus
{
    /// <summary>No successful login this run — need email + master password.</summary>
    Unauthenticated,
    /// <summary>Logged in successfully earlier this run, but the decrypted session key isn't currently held (e.g. after <see cref="BitwardenApiClient.LogOut"/>) — need just the master password.</summary>
    Locked,
    /// <summary>Currently holding a decrypted user key — the vault is usable right now.</summary>
    Unlocked,
}

/// <summary>Outcome of a login or unlock attempt.</summary>
public sealed record BitwardenAuthResult(bool Success, string? ErrorMessage, bool TwoFactorRequired = false);

/// <summary>One organization the logged-in user belongs to, with its (still RSA-wrapped) symmetric key.</summary>
internal sealed record BitwardenSyncOrganization(string Id, string WrappedKey);

/// <summary>A single vault entry from <c>/api/sync</c>, fields still as raw EncStrings (undecrypted).</summary>
internal sealed record BitwardenSyncCipher(string Id, int Type, string? OrganizationId, string? Name, string? Username, string? Password);

internal sealed record BitwardenSyncData(IReadOnlyList<BitwardenSyncOrganization> Organizations, IReadOnlyList<BitwardenSyncCipher> Ciphers);

/// <summary>
/// Talks directly to a Bitwarden/Vaultwarden server's identity + vault API and performs all
/// client-side decryption itself (see <see cref="BitwardenCrypto"/>) — issue #78's CLI-dependency
/// removal. Replaces both what <c>BitwardenCliController</c> (login/unlock via <c>bw</c>) and the
/// old <c>bw serve</c>-backed <see cref="BitwardenProvider"/> used to depend on the CLI for. Wire
/// protocol and crypto scheme verified against Vaultwarden's server source, Bitwarden's security
/// whitepaper, and the <c>bitwarden/clients</c> SDK — see the plan this was built from.
/// </summary>
public sealed class BitwardenApiClient : IDisposable
{
    // Bitwarden's own DeviceType enum (bitwarden/clients libs/common/src/enums/device-type.enum.ts) —
    // 21 = SDK, the honest category for a third-party API integration (not literally the official CLI/desktop app).
    private const string DeviceTypeSdk = "21";

    private readonly BitwardenProviderOptions _options;
    private readonly ILogger<BitwardenApiClient> _logger;
    private readonly HttpClient _http;

    // In-memory session state, valid only while this process is running.
    private string? _accessToken;
    private string? _loggedInEmail;
    private byte[]? _userKey; // 64 bytes: 32 AES key + 32 MAC key
    private RSA? _rsaPrivateKey;
    private readonly Dictionary<string, byte[]> _orgKeyCache = new();
    private int? _pendingTwoFactorProvider;

    public BitwardenApiClient(BitwardenProviderOptions options, ILogger<BitwardenApiClient> logger)
    {
        _options = options;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };
    }

    /// <summary>Current auth state, purely from in-memory session data — no network call.</summary>
    public BitwardenAuthStatus CheckStatus() =>
        _userKey is not null ? BitwardenAuthStatus.Unlocked
        : _loggedInEmail is not null ? BitwardenAuthStatus.Locked
        : BitwardenAuthStatus.Unauthenticated;

    public Task<BitwardenAuthStatus> CheckStatusAsync(CancellationToken ct = default) => Task.FromResult(CheckStatus());

    /// <summary>Logs in with an account email + master password, optionally with a 2FA code if the account requires one (see <see cref="BitwardenAuthResult.TwoFactorRequired"/>).</summary>
    public Task<BitwardenAuthResult> LoginAsync(string email, string masterPassword, string? twoFactorCode, CancellationToken ct = default) =>
        LoginCoreAsync(email, masterPassword, twoFactorCode, ct);

    /// <summary>Re-authenticates using the email from an earlier successful login this run (the "Locked" state), optionally with a 2FA code if the server asks for one again.</summary>
    public async Task<BitwardenAuthResult> UnlockAsync(string masterPassword, string? twoFactorCode, CancellationToken ct = default)
    {
        if (_loggedInEmail is null)
            return new BitwardenAuthResult(false, "No cached email from an earlier login this session — please provide your email again.");
        return await LoginCoreAsync(_loggedInEmail, masterPassword, twoFactorCode, ct).ConfigureAwait(false);
    }

    /// <summary>Clears all in-memory session state (both keys and the cached email), returning to <see cref="BitwardenAuthStatus.Unauthenticated"/>.</summary>
    public void LogOut()
    {
        _accessToken = null;
        _loggedInEmail = null;
        _userKey = null;
        _rsaPrivateKey?.Dispose();
        _rsaPrivateKey = null;
        _orgKeyCache.Clear();
    }

    private async Task<BitwardenAuthResult> LoginCoreAsync(string email, string masterPassword, string? twoFactorCode, CancellationToken ct)
    {
        BitwardenKdfConfig kdf;
        try
        {
            kdf = await PreloginAsync(email, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new BitwardenAuthResult(false, $"Could not reach the vault server for prelogin: {ex.Message}");
        }

        byte[] masterKey;
        try
        {
            masterKey = BitwardenCrypto.DeriveMasterKey(masterPassword, email, kdf);
        }
        catch (Exception ex)
        {
            return new BitwardenAuthResult(false, $"Key derivation failed: {ex.Message}");
        }

        var masterPasswordHash = BitwardenCrypto.ComputeMasterPasswordHash(masterKey, masterPassword);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "cli",
            ["username"] = email,
            ["password"] = masterPasswordHash,
            ["scope"] = "api offline_access",
            ["device_identifier"] = _options.DeviceIdentifier,
            ["device_name"] = "PolyTerm",
            ["device_type"] = DeviceTypeSdk,
        };
        if (!string.IsNullOrEmpty(twoFactorCode))
        {
            form["two_factor_token"] = twoFactorCode;
            form["two_factor_provider"] = (_pendingTwoFactorProvider ?? 0).ToString(CultureInfo.InvariantCulture);
        }

        HttpResponseMessage resp;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            resp = await _http.PostAsync("identity/connect/token", content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new BitwardenAuthResult(false, $"Could not reach the vault server: {ex.Message}");
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            return ParseLoginError(body);

        return ParseLoginSuccess(email, masterKey, body);
    }

    private BitwardenAuthResult ParseLoginError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            var twoFactorRequired = error == "invalid_grant" && description?.Contains("Two factor", StringComparison.OrdinalIgnoreCase) == true;

            if (twoFactorRequired && root.TryGetProperty("TwoFactorProviders", out var providers) && providers.GetArrayLength() > 0)
                _pendingTwoFactorProvider = int.Parse(providers[0].GetString() ?? "0", CultureInfo.InvariantCulture);

            return new BitwardenAuthResult(false, twoFactorRequired ? "Two-factor authentication code required." : (description ?? "Login failed."), twoFactorRequired);
        }
        catch (JsonException)
        {
            return new BitwardenAuthResult(false, "Login failed with an unrecognized error response.");
        }
    }

    private BitwardenAuthResult ParseLoginSuccess(string email, byte[] masterKey, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("access_token", out var tokenProp) || !root.TryGetProperty("Key", out var keyProp))
            return new BitwardenAuthResult(false, "Unexpected login response: missing access_token or Key.");

        byte[] userKey;
        try
        {
            var stretchedKey = BitwardenCrypto.StretchKey(masterKey);
            var keyEnc = BitwardenEncString.Parse(keyProp.GetString()!);
            userKey = BitwardenCrypto.DecryptSymmetric(keyEnc, stretchedKey);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return new BitwardenAuthResult(false, $"Could not decrypt the vault key: {ex.Message}");
        }

        RSA? rsaPrivateKey = null;
        if (root.TryGetProperty("PrivateKey", out var privKeyProp) && privKeyProp.GetString() is { Length: > 0 } privKeyStr)
        {
            try
            {
                var privKeyEnc = BitwardenEncString.Parse(privKeyStr);
                var privKeyBytes = BitwardenCrypto.DecryptSymmetric(privKeyEnc, userKey);
                rsaPrivateKey = RSA.Create();
                rsaPrivateKey.ImportPkcs8PrivateKey(privKeyBytes, out _);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                // Org-shared items just won't be available; personal vault items still work fine.
                _logger.LogWarning(ex, "Could not decrypt the account's RSA private key — organization-shared items won't be available.");
            }
        }

        _accessToken = tokenProp.GetString();
        _loggedInEmail = email;
        _userKey = userKey;
        _rsaPrivateKey?.Dispose();
        _rsaPrivateKey = rsaPrivateKey;
        _orgKeyCache.Clear();
        _pendingTwoFactorProvider = null;

        return new BitwardenAuthResult(true, null);
    }

    private async Task<BitwardenKdfConfig> PreloginAsync(string email, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("identity/accounts/prelogin", new { email }, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        return new BitwardenKdfConfig(
            root.GetProperty("kdf").GetInt32(),
            root.GetProperty("kdfIterations").GetInt32(),
            root.TryGetProperty("kdfMemory", out var mem) && mem.ValueKind == JsonValueKind.Number ? mem.GetInt32() : null,
            root.TryGetProperty("kdfParallelism", out var par) && par.ValueKind == JsonValueKind.Number ? par.GetInt32() : null);
    }

    /// <summary>Fetches and lightly parses <c>/api/sync</c> — ciphers and org memberships stay as raw EncStrings; decryption is the caller's job (<see cref="BitwardenProvider"/>), via <see cref="DecryptWithUserKey"/>/<see cref="TryGetOrganizationKey"/>.</summary>
    internal async Task<BitwardenSyncData> SyncAsync(CancellationToken ct = default)
    {
        if (_userKey is null) throw new InvalidOperationException("Not logged in.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/sync?excludeDomains=true");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
        var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;

        var organizations = new List<BitwardenSyncOrganization>();
        if (root.TryGetProperty("profile", out var profile) && profile.TryGetProperty("organizations", out var orgs))
        {
            foreach (var org in orgs.EnumerateArray())
            {
                if (org.TryGetProperty("id", out var idProp) && org.TryGetProperty("key", out var keyProp) && keyProp.GetString() is { Length: > 0 } key)
                    organizations.Add(new BitwardenSyncOrganization(idProp.GetString()!, key));
            }
        }

        var ciphers = new List<BitwardenSyncCipher>();
        if (root.TryGetProperty("ciphers", out var cipherArray))
        {
            foreach (var cipher in cipherArray.EnumerateArray())
            {
                var id = cipher.GetProperty("id").GetString()!;
                var type = cipher.GetProperty("type").GetInt32();
                var orgId = cipher.TryGetProperty("organizationId", out var o) ? o.GetString() : null;
                var name = cipher.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? username = null, password = null;
                if (cipher.TryGetProperty("login", out var login))
                {
                    username = login.TryGetProperty("username", out var u) ? u.GetString() : null;
                    password = login.TryGetProperty("password", out var p) ? p.GetString() : null;
                }
                ciphers.Add(new BitwardenSyncCipher(id, type, orgId, name, username, password));
            }
        }

        return new BitwardenSyncData(organizations, ciphers);
    }

    /// <summary>Decrypts an EncString field with the current user's symmetric key. Throws if not logged in.</summary>
    internal string DecryptWithUserKey(string encString) =>
        BitwardenCrypto.DecryptSymmetricString(BitwardenEncString.Parse(encString), _userKey ?? throw new InvalidOperationException("Not logged in."));

    /// <summary>
    /// Resolves (and caches) the decrypted symmetric key for an organization, from its RSA-OAEP-wrapped
    /// form in the sync response's profile — needs the RSA private key from login, so returns false
    /// if that wasn't available (account has no organizations, or its decryption failed at login).
    /// </summary>
    internal bool TryGetOrganizationKey(BitwardenSyncData sync, string organizationId, out byte[] key)
    {
        if (_orgKeyCache.TryGetValue(organizationId, out var cached))
        {
            key = cached;
            return true;
        }

        if (_rsaPrivateKey is null)
        {
            key = [];
            return false;
        }

        var org = sync.Organizations.FirstOrDefault(o => o.Id == organizationId);
        if (org is null)
        {
            key = [];
            return false;
        }

        try
        {
            var wrappedKeyEnc = BitwardenEncString.Parse(org.WrappedKey);
            key = BitwardenCrypto.DecryptAsymmetric(wrappedKeyEnc, _rsaPrivateKey);
            _orgKeyCache[organizationId] = key;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not decrypt organization {OrgId}'s key.", organizationId);
            key = [];
            return false;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _rsaPrivateKey?.Dispose();
    }
}
