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

        var result = await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken);

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

        var first = await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken);
        first.Success.Should().BeFalse();
        first.TwoFactorRequired.Should().BeTrue();
        client.CheckStatus().Should().Be(BitwardenAuthStatus.Unauthenticated, "a 2FA challenge alone must not count as logged in");

        var second = await client.LoginAsync(Email, MasterPassword, "123456", TestContext.Current.CancellationToken);
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
        var result = await client.LoginAsync(Email, "wrong password", null, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.TwoFactorRequired.Should().BeFalse();
        result.ErrorMessage.Should().Contain("incorrect");
    }

    [Fact]
    public async Task UnlockAsync_WithoutAPriorLogin_FailsWithAClearMessage()
    {
        using var server = new FakeVaultwardenServer();
        using var client = MakeClient(server.BaseUrl);

        var result = await client.UnlockAsync(MasterPassword, null, TestContext.Current.CancellationToken);

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
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        client.LogOut();
        client.CheckStatus().Should().Be(BitwardenAuthStatus.Unauthenticated, "LogOut forgets the cached email too, not just the key");

        // Re-login (simulating a fresh app session that still remembers the email some other way isn't
        // supported - LogOut is a full reset). Confirm a *successful* login puts us back in Locked-then-
        // Unlocked territory by checking UnlockAsync only works when an email is actually cached.
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        var unlockResult = await client.UnlockAsync(MasterPassword, null, TestContext.Current.CancellationToken);

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
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();

        var sync = await client.SyncAsync(TestContext.Current.CancellationToken);

        sync.Ciphers.Should().HaveCount(2);
        var login = sync.Ciphers.Single(c => c.Id == "cipher-1");
        client.DecryptWithUserKey(login.Name!).Should().Be("My Server");
        client.DecryptWithUserKey(login.Username!).Should().Be("root");
        client.DecryptWithUserKey(login.Password!).Should().Be("s3cr3t");
    }

    /// <summary>
    /// The existing "for personal items" test's non-Login cipher (type 2) simply omits the "login" key
    /// entirely, which TryGetProperty already handles fine (property not found, no crash) - it never
    /// exercised the real bug. Bitwarden's actual /api/sync response instead includes "login" with an
    /// explicit JSON null value for every non-Login cipher type (secure notes, cards, identities), which
    /// previously threw InvalidOperationException ("requires an element of type 'Object', but the
    /// target element has type 'Null'") from inside SyncAsync, before BitwardenProvider.BrowseAsync's
    /// own type filter ever got a chance to skip it - crashing the credential picker for any real vault
    /// containing anything other than a plain Login item.
    /// </summary>
    [Fact]
    public async Task SyncAsync_WithANonLoginCipherWhoseLoginFieldIsExplicitJsonNull_DoesNotThrow()
    {
        var (_, keyEncString) = BuildUserKeyEncString();
        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"fake-access-token","refresh_token":"fake-refresh-token","Key":"{{keyEncString}}"}""";
        server.SyncResponseBody = """
            {
              "profile": { "organizations": [] },
              "ciphers": [
                { "id": "secure-note-1", "type": 2, "name": null, "login": null }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();

        var act = async () => await client.SyncAsync();

        await act.Should().NotThrowAsync();
        var sync = await client.SyncAsync(TestContext.Current.CancellationToken);
        sync.Ciphers.Should().ContainSingle(c => c.Id == "secure-note-1");
    }

    /// <summary>
    /// Real, live coverage of login persistence (TryAutoUnlockAsync/RememberThisDeviceAsync/
    /// ForgetDeviceAsync) against the real OS keychain (Windows Credential Manager or the macOS login
    /// keychain), not a mock - matching this session's own
    /// LinuxUsbComPortDetectorTests/BundledX11ServerTests precedent for exercising a real OS integration
    /// rather than faking the seam away. Skips where <see cref="OsKeychainProviderTests"/> does, for the
    /// same reasons: on Linux, and on macOS under CI.
    /// </summary>
    private const string PersistenceSkipReason = "exercises the real OS keychain - only run on Windows, and on macOS outside CI.";

    [Fact]
    public async Task RememberThisDeviceAsync_ThenTryAutoUnlockAsync_OnAFreshClient_RestoresAnUnlockedSession()
    {
        Assert.SkipUnless(OsKeychainProviderTests.CanRun, PersistenceSkipReason);

        var email = UniqueEmail();
        var (userKey, keyEncString) = BuildUserKeyEncString(email);
        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"fake-access-token","refresh_token":"fake-refresh-token","Key":"{{keyEncString}}"}""";

        using var client = MakeClient(server.BaseUrl);
        try
        {
            (await client.LoginAsync(email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
            (await client.RememberThisDeviceAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

            // A SECOND, independent BitwardenApiClient instance - proves this is a real keychain
            // round-trip restoring a brand-new object's state, not just reading back the same instance.
            using var freshClient = MakeClient(server.BaseUrl);
            var restored = await freshClient.TryAutoUnlockAsync(email, TestContext.Current.CancellationToken);

            restored.Should().BeTrue();
            freshClient.CheckStatus().Should().Be(BitwardenAuthStatus.Unlocked);
            var field = EncryptForTest("hunter2", userKey);
            freshClient.DecryptWithUserKey(field).Should().Be("hunter2",
                "the restored user key should be able to decrypt a field the same way the original login's key could");
        }
        finally
        {
            await client.ForgetDeviceAsync(email, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task TryAutoUnlockAsync_WhenTheRefreshTokenIsRotated_RePersistsTheNewOne()
    {
        Assert.SkipUnless(OsKeychainProviderTests.CanRun, PersistenceSkipReason);

        var email = UniqueEmail();
        var (_, keyEncString) = BuildUserKeyEncString(email);
        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"fake-access-token","refresh_token":"original-refresh-token","Key":"{{keyEncString}}"}""";
        server.RefreshResponseBody = """{"access_token":"refreshed-access-token","refresh_token":"rotated-refresh-token"}""";

        using var client = MakeClient(server.BaseUrl);
        try
        {
            (await client.LoginAsync(email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
            (await client.RememberThisDeviceAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

            using var freshClient = MakeClient(server.BaseUrl);
            (await freshClient.TryAutoUnlockAsync(email, TestContext.Current.CancellationToken)).Should().BeTrue();

            var target = $"PolyTerm/Bitwarden/{server.BaseUrl.TrimEnd('/')}/{email.Trim().ToLowerInvariant()}";
            var keychain = new OsKeychainProvider(NullLogger<OsKeychainProvider>.Instance);
            using var stored = await keychain.ResolveAsync(
                new CredentialReference(OsKeychainProvider.Id, $"{target}|{email}"), TestContext.Current.CancellationToken);
            Encoding.UTF8.GetString(stored.Secret!).Should().Contain("rotated-refresh-token",
                "TryAutoUnlockAsync should re-persist a rotated refresh token, not leave the stale original one in the keychain");
        }
        finally
        {
            await client.ForgetDeviceAsync(email, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task TryAutoUnlockAsync_WhenTheRefreshGrantFails_ReturnsFalse_WithoutThrowing()
    {
        Assert.SkipUnless(OsKeychainProviderTests.CanRun, PersistenceSkipReason);

        var email = UniqueEmail();
        var (_, keyEncString) = BuildUserKeyEncString(email);
        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"fake-access-token","refresh_token":"fake-refresh-token","Key":"{{keyEncString}}"}""";

        using var client = MakeClient(server.BaseUrl);
        try
        {
            (await client.LoginAsync(email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
            (await client.RememberThisDeviceAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

            server.RefreshStatusCode = 400;
            server.RefreshResponseBody = """{"error":"invalid_grant"}""";

            using var freshClient = MakeClient(server.BaseUrl);
            var restored = await freshClient.TryAutoUnlockAsync(email, TestContext.Current.CancellationToken);

            restored.Should().BeFalse("a revoked/expired refresh token should fail gracefully, not throw");
            freshClient.CheckStatus().Should().Be(BitwardenAuthStatus.Unauthenticated);
        }
        finally
        {
            await client.ForgetDeviceAsync(email, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task TryAutoUnlockAsync_WithNoRememberedDevice_ReturnsFalse_WithoutThrowing()
    {
        Assert.SkipUnless(OsKeychainProviderTests.CanRun, PersistenceSkipReason);

        using var server = new FakeVaultwardenServer();
        using var client = MakeClient(server.BaseUrl);

        var restored = await client.TryAutoUnlockAsync(UniqueEmail(), TestContext.Current.CancellationToken);

        restored.Should().BeFalse();
    }

    [Fact]
    public async Task ForgetDeviceAsync_ThenTryAutoUnlockAsync_ReturnsFalse()
    {
        Assert.SkipUnless(OsKeychainProviderTests.CanRun, PersistenceSkipReason);

        var email = UniqueEmail();
        var (_, keyEncString) = BuildUserKeyEncString(email);
        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"fake-access-token","refresh_token":"fake-refresh-token","Key":"{{keyEncString}}"}""";

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        (await client.RememberThisDeviceAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        await client.ForgetDeviceAsync(email, TestContext.Current.CancellationToken);

        using var freshClient = MakeClient(server.BaseUrl);
        (await freshClient.TryAutoUnlockAsync(email, TestContext.Current.CancellationToken)).Should().BeFalse("the device was just forgotten - nothing should be left to restore");
    }

    /// <summary>Issue #384: only the user key and refresh token are remembered, so organization items used to be missing after auto-unlock.</summary>
    [Fact]
    public async Task TryAutoUnlockAsync_ThenBrowseAndResolve_IncludesOrganizationItems()
    {
        Assert.SkipUnless(OsKeychainProviderTests.CanRun, PersistenceSkipReason);

        var email = UniqueEmail();
        var (userKey, keyEncString) = BuildUserKeyEncString(email);
        var org = OrganizationVault.Create(userKey);

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}","PrivateKey":"{{org.EncryptedPrivateKey}}"}""";
        server.SyncResponseBody = org.SyncBody($$""" "privateKey": "{{org.EncryptedPrivateKey}}", """);

        using var client = MakeClient(server.BaseUrl);
        try
        {
            (await client.LoginAsync(email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
            (await client.RememberThisDeviceAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

            using var freshClient = MakeClient(server.BaseUrl);
            (await freshClient.TryAutoUnlockAsync(email, TestContext.Current.CancellationToken)).Should().BeTrue();

            await AssertOrganizationItemWorksAsync(MakeProvider(freshClient));
        }
        finally
        {
            await client.ForgetDeviceAsync(email, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Issue #384, without the keychain so it runs everywhere: with no <c>PrivateKey</c> from the token response, as
    /// after auto-unlock, the one in the sync profile is used - under either of the names a server sends it as.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrganizationItems_UseThePrivateKeyFromTheSyncProfile(bool underAccountKeys)
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();
        var org = OrganizationVault.Create(userKey);
        var profileKey = underAccountKeys
            ? $$""" "privateKey": null, "accountKeys": { "publicKeyEncryptionKeyPair": { "wrappedPrivateKey": "{{org.EncryptedPrivateKey}}" } }, """
            : $$""" "privateKey": "{{org.EncryptedPrivateKey}}", "accountKeys": null, """;

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}"}""";
        server.SyncResponseBody = org.SyncBody(profileKey);

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();

        await AssertOrganizationItemWorksAsync(MakeProvider(client));
    }

    /// <summary>Issue #384: if the key can't be restored, the user is told, and told how to log in again.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(""" "privateKey": "not-an-encstring", """)]
    [InlineData(""" "privateKey": "7.unsupported", """)]
    public async Task OrganizationItems_WithNoUsablePrivateKey_AreReported_UntilALoginSuppliesOne(string profileKey)
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();
        var org = OrganizationVault.Create(userKey);

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}"}""";
        server.SyncResponseBody = org.SyncBody(profileKey);

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        var provider = MakeProvider(client);

        var items = await provider.BrowseAsync(TestContext.Current.CancellationToken);

        items.Should().ContainSingle("the personal item still works").Which.ItemRef.Should().Be("personal");
        provider.BrowseNotice.Should().StartWith("1 organization item isn't shown").And.Contain("Log In / Unlock");
        var resolve = () => provider.ResolveAsync(new CredentialReference { ProviderId = BitwardenProvider.Id, ItemRef = "shared" });
        await resolve.Should().ThrowAsync<InvalidOperationException>().WithMessage("*organization whose key isn't available*Log In / Unlock*");

        // Logging in again while still unlocked replaces the session.
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}","PrivateKey":"{{org.EncryptedPrivateKey}}"}""";
        (await client.UnlockAsync(MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();

        await AssertOrganizationItemWorksAsync(provider);
    }

    /// <summary>One personal item and one organization item, plus the keys that go with them.</summary>
    private sealed record OrganizationVault(string EncryptedPrivateKey, string WrappedOrgKey, string PersonalName, string SharedName, string SharedPassword)
    {
        public static OrganizationVault Create(byte[] userKey)
        {
            using var rsa = RSA.Create(2048);
            var orgKey = RandomNumberGenerator.GetBytes(64);
            return new OrganizationVault(
                EncryptForTest(rsa.ExportPkcs8PrivateKey(), userKey),
                "4." + Convert.ToBase64String(rsa.Encrypt(orgKey, RSAEncryptionPadding.OaepSHA1)),
                EncryptForTest("Personal", userKey),
                EncryptForTest("Shared", orgKey),
                EncryptForTest("shared-pw", orgKey));
        }

        /// <param name="profileKey">JSON properties to put in the profile ahead of <c>organizations</c>, ending in a comma, or empty.</param>
        public string SyncBody(string profileKey) => $$"""
            {
              "profile": { {{profileKey}} "organizations": [ { "id": "org-1", "key": "{{WrappedOrgKey}}" } ] },
              "ciphers": [
                { "id": "personal", "type": 1, "name": "{{PersonalName}}" },
                { "id": "shared", "type": 1, "organizationId": "org-1", "name": "{{SharedName}}",
                  "login": { "username": null, "password": "{{SharedPassword}}" } }
              ]
            }
            """;
    }

    private static async Task AssertOrganizationItemWorksAsync(BitwardenProvider provider)
    {
        // Resolve first: a saved connection resolves its credential without ever browsing.
        using var resolved = await provider.ResolveAsync(
            new CredentialReference { ProviderId = BitwardenProvider.Id, ItemRef = "shared" }, TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(resolved.Secret!).Should().Be("shared-pw");

        var items = await provider.BrowseAsync(TestContext.Current.CancellationToken);
        items.Select(i => i.DisplayName).Should().BeEquivalentTo(["Personal", "Shared"]);
        provider.BrowseNotice.Should().BeNull();
    }

    /// <summary>
    /// Issue #379: auto-unlock can wait a long time at startup (behind a macOS keychain prompt, or on a slow
    /// server). A password login made meanwhile must survive it.
    /// </summary>
    [Fact]
    public async Task TryAutoUnlockAsync_WhenAPasswordLoginFinishesWhileItIsWaiting_KeepsThatSession()
    {
        Assert.SkipUnless(OsKeychainProviderTests.CanRun, PersistenceSkipReason);

        var email = UniqueEmail();
        var (userKey, keyEncString) = BuildUserKeyEncString(email);
        using var rsa = RSA.Create(2048);
        var orgKey = RandomNumberGenerator.GetBytes(64);
        var wrappedOrgKey = "4." + Convert.ToBase64String(rsa.Encrypt(orgKey, RSAEncryptionPadding.OaepSHA1));
        var privateKey = EncryptForTest(rsa.ExportPkcs8PrivateKey(), userKey);

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}","PrivateKey":"{{privateKey}}"}""";
        server.SyncResponseBody = $$"""
            {
              "profile": { "organizations": [ { "id": "org-1", "key": "{{wrappedOrgKey}}" } ] },
              "ciphers": [
                { "id": "shared", "type": 1, "organizationId": "org-1", "name": "{{EncryptForTest("Shared", orgKey)}}" }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        try
        {
            (await client.LoginAsync(email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
            (await client.RememberThisDeviceAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

            var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.HoldRefreshUntil = releaseRefresh.Task;

            using var freshClient = MakeClient(server.BaseUrl);
            var autoUnlock = freshClient.TryAutoUnlockAsync(email, TestContext.Current.CancellationToken);
            await server.RefreshRequested.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            (await freshClient.LoginAsync(email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
            releaseRefresh.SetResult();

            (await autoUnlock).Should().BeFalse("the vault was already unlocked by the time auto-unlock could finish");
            var items = await MakeProvider(freshClient).BrowseAsync(TestContext.Current.CancellationToken);
            items.Should().ContainSingle("the password login's organization key should still be there")
                .Which.DisplayName.Should().Be("Shared"); // this sync has no privateKey, so the key is the login's
        }
        finally
        {
            await client.ForgetDeviceAsync(email, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task TryAutoUnlockAsync_WhenTheVaultIsAlreadyUnlocked_DoesNotUseTheRememberedRefreshToken()
    {
        Assert.SkipUnless(OsKeychainProviderTests.CanRun, PersistenceSkipReason);

        var email = UniqueEmail();
        var (_, keyEncString) = BuildUserKeyEncString(email);
        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}"}""";

        using var client = MakeClient(server.BaseUrl);
        try
        {
            (await client.LoginAsync(email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
            (await client.RememberThisDeviceAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

            (await client.TryAutoUnlockAsync(email, TestContext.Current.CancellationToken)).Should().BeFalse();

            server.RefreshRequested.IsCompleted.Should().BeFalse("a rotated token would otherwise be spent for nothing");
        }
        finally
        {
            await client.ForgetDeviceAsync(email, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Issue #367: a missing <c>secret-tool</c> used to throw a <c>Win32Exception</c> that these methods didn't catch.</summary>
    [Fact]
    public async Task LoginPersistence_WhenSecretToolIsMissing_FailsWithoutCrashingTheCaller()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "OsKeychainProvider only uses secret-tool on Linux.");

        var (_, keyEncString) = BuildUserKeyEncString();
        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"fake-access-token","refresh_token":"fake-refresh-token","Key":"{{keyEncString}}"}""";
        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        LinuxSecretService.ExecutableOverride.Value = $"polyterm-no-such-tool-{Guid.NewGuid():N}";

        (await client.RememberThisDeviceAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        (await client.TryAutoUnlockAsync(Email, TestContext.Current.CancellationToken)).Should().BeFalse();
        var forget = () => client.ForgetDeviceAsync(Email, TestContext.Current.CancellationToken);
        await forget.Should().ThrowAsync<InvalidOperationException>("it can't tell whether an entry is still stored");
    }

    // ---- BitwardenProvider over a real sync payload: per-item (cipher) keys, issue #200 ----

    [Fact]
    public async Task BrowseAsync_PersonalItems_DecryptsBothLegacyItemsAndItemsWithTheirOwnKey()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();
        var itemKey = RandomNumberGenerator.GetBytes(64);

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}"}""";
        server.SyncResponseBody = $$"""
            {
              "profile": { "organizations": [] },
              "ciphers": [
                { "id": "legacy", "type": 1, "key": null, "name": "{{EncryptForTest("Legacy Server", userKey)}}",
                  "login": { "username": "{{EncryptForTest("root", userKey)}}", "password": "{{EncryptForTest("old-pw", userKey)}}" } },
                { "id": "keyed", "type": 1, "key": "{{EncryptForTest(itemKey, userKey)}}", "name": "{{EncryptForTest("Keyed Server", itemKey)}}",
                  "login": { "username": "{{EncryptForTest("admin", itemKey)}}", "password": "{{EncryptForTest("new-pw", itemKey)}}" } }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        var provider = MakeProvider(client);

        var items = await provider.BrowseAsync(TestContext.Current.CancellationToken);

        items.Should().BeEquivalentTo(new[]
        {
            new { ItemRef = "legacy", DisplayName = "Legacy Server", Username = "root" },
            new { ItemRef = "keyed", DisplayName = "Keyed Server", Username = "admin" },
        });
        var resolved = await provider.ResolveAsync(new CredentialReference { ProviderId = BitwardenProvider.Id, ItemRef = "keyed" }, TestContext.Current.CancellationToken);
        resolved.Username.Should().Be("admin");
        Encoding.UTF8.GetString(resolved.Secret!).Should().Be("new-pw");
    }

    /// <summary>The shape of the crash in issue #200: an organization item whose fields are encrypted with its own key, which in turn is encrypted with the org key.</summary>
    [Fact]
    public async Task BrowseAsync_OrganizationItems_DecryptsBothLegacyItemsAndItemsWithTheirOwnKey()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();
        using var rsa = RSA.Create(2048);
        var orgKey = RandomNumberGenerator.GetBytes(64);
        var wrappedOrgKey = "4." + Convert.ToBase64String(rsa.Encrypt(orgKey, RSAEncryptionPadding.OaepSHA1));
        var privateKey = EncryptForTest(rsa.ExportPkcs8PrivateKey(), userKey);
        var itemKey = RandomNumberGenerator.GetBytes(64);

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}","PrivateKey":"{{privateKey}}"}""";
        server.SyncResponseBody = $$"""
            {
              "profile": { "organizations": [ { "id": "org-1", "key": "{{wrappedOrgKey}}" } ] },
              "ciphers": [
                { "id": "legacy", "type": 1, "organizationId": "org-1", "name": "{{EncryptForTest("Legacy Shared", orgKey)}}",
                  "login": { "username": "{{EncryptForTest("root", orgKey)}}", "password": "{{EncryptForTest("old-pw", orgKey)}}" } },
                { "id": "keyed", "type": 1, "organizationId": "org-1", "key": "{{EncryptForTest(itemKey, orgKey)}}",
                  "name": "{{EncryptForTest("Keyed Shared", itemKey)}}",
                  "login": { "username": "{{EncryptForTest("admin", itemKey)}}", "password": "{{EncryptForTest("new-pw", itemKey)}}" } }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        var provider = MakeProvider(client);

        var items = await provider.BrowseAsync(TestContext.Current.CancellationToken);

        items.Should().BeEquivalentTo(new[]
        {
            new { ItemRef = "legacy", DisplayName = "Legacy Shared", Username = "root" },
            new { ItemRef = "keyed", DisplayName = "Keyed Shared", Username = "admin" },
        });
        var resolved = await provider.ResolveAsync(new CredentialReference { ProviderId = BitwardenProvider.Id, ItemRef = "keyed" }, TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(resolved.Secret!).Should().Be("new-pw");
    }

    [Fact]
    public async Task BrowseAsync_SkipsAnItemThatFailsToDecrypt_AndStillListsTheRest()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();
        var wrongKey = RandomNumberGenerator.GetBytes(64);

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}"}""";
        server.SyncResponseBody = $$"""
            {
              "profile": { "organizations": [] },
              "ciphers": [
                { "id": "corrupt", "type": 1, "name": "{{EncryptForTest("Unreadable", wrongKey)}}",
                  "login": { "username": null, "password": "{{EncryptForTest("pw", wrongKey)}}" } },
                { "id": "bad-item-key", "type": 1, "key": "not-an-encstring", "name": "{{EncryptForTest("Also Unreadable", userKey)}}" },
                { "id": "good", "type": 1, "name": "{{EncryptForTest("Good Server", userKey)}}",
                  "login": { "username": null, "password": "{{EncryptForTest("pw", userKey)}}" } }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        var provider = MakeProvider(client);

        var items = await provider.BrowseAsync(TestContext.Current.CancellationToken);

        items.Should().ContainSingle().Which.ItemRef.Should().Be("good");

        var resolve = () => provider.ResolveAsync(new CredentialReference { ProviderId = BitwardenProvider.Id, ItemRef = "corrupt" });
        (await resolve.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Couldn't decrypt Bitwarden item 'corrupt'.")
            .WithInnerException<CryptographicException>();
    }

    // ---- SSH Key items (cipher type 5), issue #283 ----

    private const string TestPrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nnot-a-real-key\n-----END OPENSSH PRIVATE KEY-----\n";

    [Fact]
    public async Task SshKeyItem_Personal_BrowsesAsAKeyWithItsFingerprint_AndResolvesToPrivateKeyOnly()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}"}""";
        server.SyncResponseBody = $$"""
            {
              "profile": { "organizations": [] },
              "ciphers": [
                { "id": "login", "type": 1, "name": "{{EncryptForTest("Web Server", userKey)}}", "sshKey": null,
                  "login": { "username": "{{EncryptForTest("root", userKey)}}", "password": "{{EncryptForTest("pw", userKey)}}" } },
                { "id": "key", "type": 5, "name": "{{EncryptForTest("Deploy Key", userKey)}}", "login": null,
                  "sshKey": { "privateKey": "{{EncryptForTest(TestPrivateKey, userKey)}}",
                              "publicKey": "{{EncryptForTest("ssh-ed25519 AAAA", userKey)}}",
                              "keyFingerprint": "{{EncryptForTest("SHA256:abc123", userKey)}}" } }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        var provider = MakeProvider(client);

        var items = await provider.BrowseAsync(TestContext.Current.CancellationToken);

        items.Should().BeEquivalentTo(new[]
        {
            new { ItemRef = "login", DisplayName = "Web Server", Username = (string?)"root", Kind = CredentialKind.Password, KeyFingerprint = (string?)null },
            new { ItemRef = "key", DisplayName = "Deploy Key", Username = (string?)null, Kind = CredentialKind.SshKey, KeyFingerprint = (string?)"SHA256:abc123" },
        });

        using var resolved = await provider.ResolveAsync(new CredentialReference { ProviderId = BitwardenProvider.Id, ItemRef = "key" }, TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(resolved.PrivateKey!).Should().Be(TestPrivateKey);
        resolved.Secret.Should().BeNull();
        resolved.Username.Should().BeNull();
    }

    [Fact]
    public async Task SshKeyItem_InAnOrganization_WithItsOwnItemKey_ResolvesToPrivateKey()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();
        using var rsa = RSA.Create(2048);
        var orgKey = RandomNumberGenerator.GetBytes(64);
        var wrappedOrgKey = "4." + Convert.ToBase64String(rsa.Encrypt(orgKey, RSAEncryptionPadding.OaepSHA1));
        var privateKey = EncryptForTest(rsa.ExportPkcs8PrivateKey(), userKey);
        var itemKey = RandomNumberGenerator.GetBytes(64);

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}","PrivateKey":"{{privateKey}}"}""";
        server.SyncResponseBody = $$"""
            {
              "profile": { "organizations": [ { "id": "org-1", "key": "{{wrappedOrgKey}}" } ] },
              "ciphers": [
                { "id": "key", "type": 5, "organizationId": "org-1", "key": "{{EncryptForTest(itemKey, orgKey)}}",
                  "name": "{{EncryptForTest("Shared Key", itemKey)}}", "login": null,
                  "sshKey": { "privateKey": "{{EncryptForTest(TestPrivateKey, itemKey)}}",
                              "publicKey": "{{EncryptForTest("ssh-ed25519 AAAA", itemKey)}}",
                              "keyFingerprint": "{{EncryptForTest("SHA256:shared", itemKey)}}" } }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        var provider = MakeProvider(client);

        var items = await provider.BrowseAsync(TestContext.Current.CancellationToken);

        items.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { ItemRef = "key", DisplayName = "Shared Key", Kind = CredentialKind.SshKey, KeyFingerprint = "SHA256:shared" });
        using var resolved = await provider.ResolveAsync(new CredentialReference { ProviderId = BitwardenProvider.Id, ItemRef = "key" }, TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(resolved.PrivateKey!).Should().Be(TestPrivateKey);
        resolved.Secret.Should().BeNull();
    }

    [Fact]
    public async Task SshKeyItem_WithANullSshKeyObject_IsSkipped_AndCannotBeResolved()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}"}""";
        server.SyncResponseBody = $$"""
            {
              "profile": { "organizations": [] },
              "ciphers": [
                { "id": "empty-key", "type": 5, "name": "{{EncryptForTest("Broken Key", userKey)}}", "login": null, "sshKey": null },
                { "id": "good", "type": 1, "name": "{{EncryptForTest("Good Server", userKey)}}",
                  "login": { "username": null, "password": "{{EncryptForTest("pw", userKey)}}" } }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();
        var provider = MakeProvider(client);

        var items = await provider.BrowseAsync(TestContext.Current.CancellationToken);

        items.Should().ContainSingle().Which.ItemRef.Should().Be("good");
        var resolve = () => provider.ResolveAsync(new CredentialReference { ProviderId = BitwardenProvider.Id, ItemRef = "empty-key" });
        (await resolve.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Bitwarden item 'empty-key' has no private key to supply.");
    }

    // ---- Where each item lives: personal vault/folder or organization/collections, issue #285 ----

    [Fact]
    public async Task BrowseAsync_PersonalItems_ShowMyVault_AndTheFolderWhenThereIsOne()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}"}""";
        server.SyncResponseBody = $$"""
            {
              "profile": { "organizations": [] },
              "folders": [ { "id": "folder-1", "name": "{{EncryptForTest("Servers", userKey)}}" } ],
              "collections": [],
              "ciphers": [
                { "id": "loose", "type": 1, "folderId": null, "collectionIds": [], "name": "{{EncryptForTest("Loose", userKey)}}" },
                { "id": "filed", "type": 1, "folderId": "folder-1", "collectionIds": [], "name": "{{EncryptForTest("Filed", userKey)}}" },
                { "id": "filed-too", "type": 1, "folderId": "folder-1", "name": "{{EncryptForTest("Filed Too", userKey)}}" }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();

        var items = await MakeProvider(client).BrowseAsync(TestContext.Current.CancellationToken);

        items.Should().BeEquivalentTo(new[]
        {
            new { ItemRef = "loose", Location = "My vault" },
            new { ItemRef = "filed", Location = "My vault › Servers" },
            new { ItemRef = "filed-too", Location = "My vault › Servers" },
        });
    }

    [Fact]
    public async Task BrowseAsync_OrganizationItems_ShowTheOrgAndEveryCollection_WithAFallbackForAnUndecryptableName()
    {
        var (userKey, keyEncString) = BuildUserKeyEncString();
        using var rsa = RSA.Create(2048);
        var orgKey = RandomNumberGenerator.GetBytes(64);
        var wrongKey = RandomNumberGenerator.GetBytes(64);
        var wrappedOrgKey = "4." + Convert.ToBase64String(rsa.Encrypt(orgKey, RSAEncryptionPadding.OaepSHA1));
        var privateKey = EncryptForTest(rsa.ExportPkcs8PrivateKey(), userKey);

        using var server = new FakeVaultwardenServer();
        server.TokenResponseBody = $$"""{"access_token":"a","refresh_token":"r","Key":"{{keyEncString}}","PrivateKey":"{{privateKey}}"}""";
        server.SyncResponseBody = $$"""
            {
              "profile": { "organizations": [ { "id": "org-1", "name": "Automation", "key": "{{wrappedOrgKey}}" } ] },
              "folders": [],
              "collections": [
                { "id": "col-infra", "organizationId": "org-1", "name": "{{EncryptForTest("infra", orgKey)}}" },
                { "id": "col-health", "organizationId": "org-1", "name": "{{EncryptForTest("health", orgKey)}}" },
                { "id": "col-broken", "organizationId": "org-1", "name": "{{EncryptForTest("secret", wrongKey)}}" }
              ],
              "ciphers": [
                { "id": "one", "type": 1, "organizationId": "org-1", "collectionIds": [ "col-infra" ],
                  "name": "{{EncryptForTest("One", orgKey)}}" },
                { "id": "several", "type": 1, "organizationId": "org-1", "collectionIds": [ "col-infra", "col-health" ],
                  "name": "{{EncryptForTest("Several", orgKey)}}" },
                { "id": "broken", "type": 1, "organizationId": "org-1", "collectionIds": [ "col-broken" ],
                  "name": "{{EncryptForTest("Broken Collection", orgKey)}}" },
                { "id": "none", "type": 1, "organizationId": "org-1", "collectionIds": [],
                  "name": "{{EncryptForTest("No Collection", orgKey)}}" }
              ]
            }
            """;

        using var client = MakeClient(server.BaseUrl);
        (await client.LoginAsync(Email, MasterPassword, null, TestContext.Current.CancellationToken)).Success.Should().BeTrue();

        var items = await MakeProvider(client).BrowseAsync(TestContext.Current.CancellationToken);

        items.Should().BeEquivalentTo(new[]
        {
            new { ItemRef = "one", Location = "Automation › infra" },
            new { ItemRef = "several", Location = "Automation › health, infra" },
            new { ItemRef = "broken", Location = "Automation › (unknown collection)" },
            new { ItemRef = "none", Location = "Automation" },
        });
    }

    private static BitwardenProvider MakeProvider(BitwardenApiClient client) =>
        new(client, NullLogger<BitwardenProvider>.Instance);

    private static BitwardenApiClient MakeClient(string baseUrl) =>
        new(new BitwardenProviderOptions { BaseUrl = baseUrl, DeviceIdentifier = Guid.NewGuid().ToString() },
            new OsKeychainProvider(NullLogger<OsKeychainProvider>.Instance),
            NullLogger<BitwardenApiClient>.Instance);

    /// <summary>A fresh, unique email per test that needs a real OS-keychain round-trip, so parallel test runs never collide on the same keychain target.</summary>
    private static string UniqueEmail() => $"test-{Guid.NewGuid():N}@example.com";

    /// <summary>
    /// Builds a "Key" EncString the same way a real server would: a random 64-byte user symmetric key,
    /// AES-CBC+HMAC-encrypted with the *actual* StretchedMasterKey BitwardenApiClient will
    /// independently derive from <paramref name="email"/>/<see cref="MasterPassword"/> under the fake
    /// server's advertised (fast, test-only) KDF params - so a real decrypt has to succeed for these
    /// tests to pass, not just a canned response shape.
    /// </summary>
    private static (byte[] UserKey, string KeyEncString) BuildUserKeyEncString(string email = Email)
    {
        var masterKey = BitwardenCrypto.DeriveMasterKey(MasterPassword, email, FakeVaultwardenServer.Kdf);
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

    /// <summary>Response for a <c>grant_type=refresh_token</c> request specifically - distinct from <see cref="TokenResponseBody"/>, which only ever handles the initial password grant.</summary>
    public int RefreshStatusCode { get; set; } = 200;
    public string RefreshResponseBody { get; set; } = """{"access_token":"refreshed-access-token","refresh_token":"refreshed-refresh-token"}""";

    /// <summary>When set, a refresh request isn't answered until this completes; other requests are still served meanwhile.</summary>
    public Task? HoldRefreshUntil { get; set; }

    private readonly TaskCompletionSource _refreshRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a refresh request has arrived.</summary>
    public Task RefreshRequested => _refreshRequested.Task;

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
                "/api/sync" => (200, FilterSshKeysForOldClients(SyncResponseBody, ctx.Request.Headers["Bitwarden-Client-Version"])),
                _ => (404, "not found"),
            };

            if (body.Contains("grant_type=refresh_token"))
            {
                _refreshRequested.TrySetResult();
                if (HoldRefreshUntil is { } release)
                {
                    _ = RespondAfterAsync(release, ctx, status, response);
                    continue;
                }
            }

            await RespondAsync(ctx, status, response).ConfigureAwait(false);
        }
    }

    private async Task RespondAfterAsync(Task release, HttpListenerContext ctx, int status, string response)
    {
        await release.WaitAsync(_cts.Token).ConfigureAwait(false);
        await RespondAsync(ctx, status, response).ConfigureAwait(false);
    }

    private async Task RespondAsync(HttpListenerContext ctx, int status, string response)
    {
        ctx.Response.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(response);
        await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
        ctx.Response.Close();
    }

    /// <summary>
    /// Mirrors Vaultwarden's <c>sync</c> handler (and Bitwarden's): SSH Key items (type 5) are dropped unless the
    /// client reports version 2024.12.0 or later, and a missing header counts as too old (issue #283).
    /// </summary>
    private static string FilterSshKeysForOldClients(string syncBody, string? clientVersion)
    {
        if (Version.TryParse(clientVersion, out var version) && version >= new Version(2024, 12, 0))
            return syncBody;

        var root = System.Text.Json.Nodes.JsonNode.Parse(syncBody)!.AsObject();
        if (root["ciphers"] is System.Text.Json.Nodes.JsonArray ciphers)
        {
            foreach (var cipher in ciphers.Where(c => c?["type"]?.GetValue<int>() == 5).ToList())
                ciphers.Remove(cipher);
        }
        return root.ToJsonString();
    }

    private (int Status, string Body) HandleToken(string formBody)
    {
        if (formBody.Contains("grant_type=refresh_token"))
            return (RefreshStatusCode, RefreshResponseBody);

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
