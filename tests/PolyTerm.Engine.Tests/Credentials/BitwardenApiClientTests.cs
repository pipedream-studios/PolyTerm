using System.Net;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine.Credentials;
using Xunit;

namespace PolyTerm.Engine.Tests.Credentials;

/// <summary>
/// Drives <see cref="BitwardenApiClient"/> against a real local HTTP server (a hand-rolled fake
/// Vaultwarden identity/sync API, not a mock), matching this repo's general testing convention (see
/// issue #73's own real-<c>HttpListener</c>-backed diagnostic tests) — verifies prelogin/login/
/// 2FA-retry/sync request construction and response parsing without needing a live Bitwarden/
/// Vaultwarden server. The one thing this can't substitute for: whether the real wire format assumed
/// here (verified against Vaultwarden's own source — see the plan this was built from) actually
/// matches a real server. That needs a real end-to-end test against an actual vault.
/// </summary>
public sealed class BitwardenApiClientTests
{
    private const string Email = "test@example.com";
    private const string MasterPassword = "correct horse battery staple";

    [Fact]
    public async Task LoginAsync_Success_DecryptsTheUserKey_AndUnlocksTheSession()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();
        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"fake-access-token","refresh_token":"fake-refresh-token","Key":"{{keyEncString}}"}""";

        using var client = MakeClient(server.BaseUrl);
        client.CheckStatus().Should().Be(BitwardenAuthStatus.Unauthenticated);

        var result = await client.LoginAsync(Email, MasterPassword, null);

        result.Success.Should().BeTrue();
        client.CheckStatus().Should().Be(BitwardenAuthStatus.Unlocked);

        // Prove the full chain actually worked: decrypt a field with the resulting user key and
        // confirm it matches what was encrypted with the *expected* key computed independently above.
        var field = EncryptForTest("hunter2", userKey);
        client.DecryptWithUserKey(field).Should().Be("hunter2");
    }

    [Fact]
    public async Task LoginAsync_TwoFactorRequired_ThenSucceedsOnRetryWithCode()
    {
        var (_, keyEncString) = BuildUserKeyEncString();
        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"fake-access-token","refresh_token":"fake-refresh-token","Key":"{{keyEncString}}"}""";
        server.RequireTwoFactor = true;

        using var client = MakeClient(server.BaseUrl);

        var first = await client.LoginAsync(Email, MasterPassword, null);
        first.Success.Should().BeFalse();
        first.TwoFactorRequired.Should().BeTrue();
        client.CheckStatus().Should().Be(BitwardenAuthStatus.Unauthenticated, "a 2FA challenge alone must not count as logged in");

        var second = await client.LoginAsync(Email, MasterPassword, "123456");
        second.Success.Should().BeTrue();
        client.CheckStatus().Should().Be(BitwardenAuthStatus.Unlocked);
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_ReportsFailure_NotTwoFactorRequired()
    {
        using var server = new FakeVaultwardenServer();
        server.TokenStatusCode = (int)HttpStatusCode.BadRequest;
        server.TokenResponseBody = """{"error":"invalid_grant","error_description":"Username or password is incorrect. Try again"}""";

        using var client = MakeClient(server.BaseUrl);
        var result = await client.LoginAsync(Email, "wrong password", null);

        result.Success.Should().BeFalse();
        result.TwoFactorRequired.Should().BeFalse();
        result.ErrorMessage.Should().Contain("incorrect");
    }

    [Fact]
    public async Task UnlockAsync_WithoutAPriorLogin_FailsWithAClearMessage()
    {
        using var server = new FakeVaultwardenServer();
        using var client = MakeClient(server.BaseUrl);

        var result = await client.UnlockAsync(MasterPassword, null);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("email");
    }

    [Fact]
    public async Task UnlockAsync_AfterAnEarlierLogin_ReAuthenticatesWithTheCachedEmail()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();
        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"fake-access-token","refresh_token":"fake-refresh-token","Key":"{{keyEncString}}"}""";

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null)).Success.Should().BeTrue();
        client.LogOut();
        client.CheckStatus().Should().Be(BitwardenAuthStatus.Unauthenticated, "LogOut forgets the cached email too, not just the key");

        // Re-login (simulating a fresh app session that still remembers the email some other way isn't
        // supported - LogOut is a full reset). Confirm a *successful* login puts us back in Locked-then-
        // Unlocked territory by checking UnlockAsync only works when an email is actually cached.
        (await client.LoginAsync(Email, MasterPassword, null)).Success.Should().BeTrue();
        var unlockResult = await client.UnlockAsync(MasterPassword, null);

        unlockResult.Success.Should().BeTrue("UnlockAsync should re-run login using the email cached from the earlier successful LoginAsync call");
        _ = userKey; // already exercised by the full-chain assertion in the first test
    }

    [Fact]
    public async Task SyncAsync_DecryptsCipherFields_ForPersonalItems()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();
        var encryptedName = EncryptForTest("My Server", userKey);
        var encryptedUsername = EncryptForTest("root", userKey);
        var encryptedPassword = EncryptForTest("s3cr3t", userKey);

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"fake-access-token","refresh_token":"fake-refresh-token","Key":"{{keyEncString}}"}""";
        server.SyncResponseBody = $$"""
            {
              "profile": { "organizations": [] },
              "ciphers": [
                { "id": "cipher-1", "type": 1, "name": "{{encryptedName}}",
                  "login": { "username": "{{encryptedUsername}}", "password": "{{encryptedPassword}}" } },
                { "id": "cipher-2", "type": 2, "name": "{{encryptedName}}" }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null)).Success.Should().BeTrue();

        var sync = await client.SyncAsync();

        sync.Ciphers.Should().HaveCount(2);
        var login = sync.Ciphers.Single(c => c.Id == "cipher-1");
        client.DecryptWithUserKey(login.Name!).Should().Be("My Server");
        client.DecryptWithUserKey(login.Username!).Should().Be("root");
        client.DecryptWithUserKey(login.Password!).Should().Be("s3cr3t");
    }

    private static BitwardenApiClient MakeClient(string baseUrl) =>
        new(new BitwardenProviderOptions { BaseUrl = baseUrl, DeviceIdentifier = Guid.NewGuid().ToString() }, NullLogger<BitwardenApiClient>.Instance);

    /// <summary>
    /// Builds a "Key" EncString the same way a real server would: a random 64-byte user symmetric key,
    /// AES-CBC+HMAC-encrypted with the *actual* StretchedMasterKey BitwardenApiClient will
    /// independently derive from <see cref="Email"/>/<see cref="MasterPassword"/> under the fake
    /// server's advertised (fast, test-only) KDF params - so a real decrypt has to succeed for these
    /// tests to pass, not just a canned response shape.
    /// </summary>
    private static (byte[] UserKey, string KeyEncString) BuildUserKeyEncString()
    {
        var masterKey = BitwardenCrypto.DeriveMasterKey(MasterPassword, Email, FakeVaultwardenServer.Kdf);
        var stretchedKey = BitwardenCrypto.StretchKey(masterKey);
        var userKey = RandomNumberGenerator.GetBytes(64);
        var keyEncString = EncryptForTest(userKey, stretchedKey);
        return (userKey, keyEncString);
    }

    private static string EncryptForTest(string plaintext, byte[] key) => EncryptForTest(Encoding.UTF8.GetBytes(plaintext), key);

    private static string EncryptForTest(byte[] plainBytes, byte[] key)
    {
        var encKey = key[..32];
        var macKey = key[32..];

        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var macInput = new byte[aes.IV.Length + cipherBytes.Length];
        aes.IV.CopyTo(macInput, 0);
        cipherBytes.CopyTo(macInput, aes.IV.Length);
        var mac = HMACSHA256.HashData(macKey, macInput);

        return $"2.{Convert.ToBase64String(aes.IV)}|{Convert.ToBase64String(cipherBytes)}|{Convert.ToBase64String(mac)}";
    }
}

/// <summary>
/// A minimal, real HTTP server standing in for a Vaultwarden/Bitwarden identity+sync API, routing by
/// path. Canned responses are configurable per test via the public properties; defaults are a
/// successful, fast (low-iteration) PBKDF2 KDF config and an empty sync.
/// </summary>
file sealed class FakeVaultwardenServer : IDisposable
{
    // Deliberately tiny iteration count - correctness of PBKDF2 itself is BitwardenCryptoTests' job;
    // this just needs to be fast and match what BuildUserKeyEncString derives against.
    public static readonly BitwardenKdfConfig Kdf = new(0, 5, null, null);

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public string BaseUrl { get; }
    public int TokenStatusCode { get; set; } = 200;
    public string TokenResponseBody { get; set; } = """{"access_token":"unused","refresh_token":"unused"}""";
    public string SyncResponseBody { get; set; } = """{"profile":{"organizations":[]},"ciphers":[]}""";
    public bool RequireTwoFactor { get; set; }

    public FakeVaultwardenServer()
    {
        var port = GetFreeTcpPort();
        BaseUrl = $"http://localhost:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or HttpListenerException)
            {
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);

            var (status, response) = ctx.Request.Url!.AbsolutePath switch
            {
                "/identity/accounts/prelogin" => (200, $$"""{"kdf":{{Kdf.KdfType}},"kdfIterations":{{Kdf.Iterations}},"kdfMemory":null,"kdfParallelism":null}"""),
                "/identity/connect/token" => HandleToken(body),
                "/api/sync" => (200, SyncResponseBody),
                _ => (404, "not found"),
            };

            ctx.Response.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(response);
            await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }

    private (int Status, string Body) HandleToken(string formBody)
    {
        if (RequireTwoFactor && !formBody.Contains("two_factor_token="))
        {
            return (400, """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0"]}""");
        }
        return (TokenStatusCode, TokenResponseBody);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _listener.Close();
        _cts.Dispose();
    }
}
