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
/// Everything needed to silently restore a session without the master password (issue: Bitwarden login
/// persistence) — persisted as one JSON blob in the OS keychain, opt-in via "Remember this device".
/// <see cref="UserKeyBase64"/> alone can't refresh a stale access token, and <see cref="RefreshToken"/>
/// alone can't decrypt the vault - both are needed together.
/// </summary>
internal sealed record RememberedBitwardenDevice(string UserKeyBase64, string RefreshToken);

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
    private readonly OsKeychainProvider _keychain;
    private readonly ILogger<BitwardenApiClient> _logger;
    private readonly HttpClient _http;

    // In-memory session state, valid only while this process is running - except _refreshToken and
    // _userKey, which are also (opt-in) persisted to the OS keychain via RememberThisDeviceAsync so
    // TryAutoUnlockAsync can restore a session on a future run without the master password.
    private string? _accessToken;
    private string? _refreshToken;
    private string? _loggedInEmail;
    private byte[]? _userKey; // 64 bytes: 32 AES key + 32 MAC key
    private RSA? _rsaPrivateKey;
    private readonly Dictionary<string, byte[]> _orgKeyCache = new();
    private int? _pendingTwoFactorProvider;

    public BitwardenApiClient(BitwardenProviderOptions options, OsKeychainProvider keychain, ILogger<BitwardenApiClient> logger)
    {
        _options = options;
        _keychain = keychain;
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

    /// <summary>
    /// Clears all in-memory session state (both keys and the cached email), returning to
    /// <see cref="BitwardenAuthStatus.Unauthenticated"/>. Deliberately does NOT touch anything persisted
    /// by <see cref="RememberThisDeviceAsync"/> - locking/logging out mid-session must not silently opt
    /// the machine out of "remember me" behind the user's back. Use <see cref="ForgetDeviceAsync"/> to
    /// actually remove the persisted entry.
    /// </summary>
    public void LogOut()
    {
        _accessToken = null;
        _refreshToken = null;
        _loggedInEmail = null;
        _userKey = null;
        _rsaPrivateKey?.Dispose();
        _rsaPrivateKey = null;
        _orgKeyCache.Clear();
    }

    /// <summary>
    /// Attempts to silently restore a session from an OS-keychain-persisted "Remember this device" entry
    /// (issue: Bitwarden login persistence) - no master password, no prompt. Safe to call unconditionally
    /// at startup: returns false (never throws) for every failure mode - no entry ever written, a corrupt
    /// blob, a revoked/expired refresh token, no network, or an unsupported platform (macOS, or Linux
    /// without <c>secret-tool</c> installed).
    /// </summary>
    public async Task<bool> TryAutoUnlockAsync(string email, CancellationToken ct = default)
    {
        RememberedBitwardenDevice? remembered;
        try
        {
            var reference = new CredentialReference(OsKeychainProvider.Id, $"{BuildKeychainTarget(email)}|{email}");
            using var material = await _keychain.ResolveAsync(reference, ct).ConfigureAwait(false);
            if (material.Secret is null) return false;
            remembered = JsonSerializer.Deserialize<RememberedBitwardenDevice>(material.Secret);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException or JsonException)
        {
            _logger.LogDebug(ex, "No remembered Bitwarden device to restore for {Email}.", email);
            return false;
        }
        if (remembered is null) return false;

        var refreshed = await RefreshAccessTokenAsync(remembered.RefreshToken, ct).ConfigureAwait(false);
        if (refreshed is null) return false;

        byte[] userKey;
        try
        {
            userKey = Convert.FromBase64String(remembered.UserKeyBase64);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Remembered Bitwarden device entry for {Email} has a corrupt key; ignoring it.", email);
            return false;
        }

        _accessToken = refreshed.Value.AccessToken;
        _refreshToken = refreshed.Value.RefreshToken ?? remembered.RefreshToken;
        _userKey = userKey;
        _loggedInEmail = email;
        // Org-shared items need a full interactive login to decrypt again - see this class's own doc
        // comment on RememberedBitwardenDevice for why only the user key + refresh token are persisted.
        _rsaPrivateKey?.Dispose();
        _rsaPrivateKey = null;
        _orgKeyCache.Clear();

        // Re-persist if the server rotated the refresh token, so the next restart uses the live one.
        if (refreshed.Value.RefreshToken is not null && refreshed.Value.RefreshToken != remembered.RefreshToken)
            await RememberThisDeviceAsync(ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Persists enough of the current session to the OS keychain (Credential Manager on Windows, Secret
    /// Service on Linux) that <see cref="TryAutoUnlockAsync"/> can silently restore it on a future run
    /// with no master password - "Remember this device". Best-effort: returns false (logged, never
    /// thrown) rather than surfacing a blocking error to the caller, since failing to remember shouldn't
    /// fail the login itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not currently <see cref="BitwardenAuthStatus.Unlocked"/>.</exception>
    public async Task<bool> RememberThisDeviceAsync(CancellationToken ct = default)
    {
        if (_userKey is null || _loggedInEmail is null)
            throw new InvalidOperationException("Cannot remember this device before a successful login/unlock this run.");
        if (_refreshToken is null)
        {
            _logger.LogWarning("No refresh token from this login - cannot remember this device.");
            return false;
        }

        var blob = new RememberedBitwardenDevice(Convert.ToBase64String(_userKey), _refreshToken);
        var json = JsonSerializer.Serialize(blob);

        try
        {
            await _keychain.WriteAsync(BuildKeychainTarget(_loggedInEmail), _loggedInEmail, json, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not remember this device in the OS keychain.");
            return false;
        }
    }

    /// <summary>Removes a device remembered via <see cref="RememberThisDeviceAsync"/>. Best-effort: swallows an unsupported platform or a nonexistent entry, both treated as already-forgotten.</summary>
    public async Task ForgetDeviceAsync(string email, CancellationToken ct = default)
    {
        try
        {
            await OsKeychainProvider.DeleteAsync(BuildKeychainTarget(email), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Nothing to forget for {Email} on this platform.", email);
        }
    }

    /// <summary>
    /// Exchanges a refresh token for a fresh access token via the OAuth refresh grant - the only way to
    /// get a live access token without re-deriving the master key, used by <see cref="TryAutoUnlockAsync"/>.
    /// Returns null (never throws) on any non-2xx response, network failure, or unparseable body - a
    /// revoked/expired refresh token is exactly as unremarkable as "no persisted device" from the
    /// caller's perspective.
    /// </summary>
    private async Task<(string AccessToken, string? RefreshToken)?> RefreshAccessTokenAsync(string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "cli",
            ["refresh_token"] = refreshToken,
            ["device_identifier"] = _options.DeviceIdentifier,
            ["device_name"] = "PolyTerm",
            ["device_type"] = DeviceTypeSdk,
        };

        HttpResponseMessage resp;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            resp = await _http.PostAsync("identity/connect/token", content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Could not reach the vault server to refresh the access token.");
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogDebug("Refresh token grant failed with {StatusCode}.", resp.StatusCode);
            return null;
        }

        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var tokenProp)) return null;
            var newRefreshToken = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            return (tokenProp.GetString()!, newRefreshToken);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Refresh token grant returned an unparseable response.");
            return null;
        }
    }

    /// <summary>Stable, per-account-and-server OS keychain target - scoping by both means switching either just orphans the old entry rather than colliding with or overwriting it.</summary>
    private string BuildKeychainTarget(string email) =>
        $"PolyTerm/Bitwarden/{_options.BaseUrl.TrimEnd('/')}/{email.Trim().ToLowerInvariant()}";

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
        // "offline_access" is already requested in the login form's scope specifically so the server
        // includes this - previously parsed by nothing and silently discarded.
        _refreshToken = root.TryGetProperty("refresh_token", out var refreshProp) ? refreshProp.GetString() : null;
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
                // Bitwarden's real /api/sync response includes a "login" key set to JSON null for
                // every non-Login cipher type (secure notes, cards, identities) - only a genuine
                // Login item's "login" is actually an object. TryGetProperty throws if called on a
                // non-object element, so this has to check ValueKind before descending into it, even
                // though BrowseAsync filters non-Login ciphers out right after this - that filtering
                // happens too late to protect this parse.
                if (cipher.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.Object)
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
