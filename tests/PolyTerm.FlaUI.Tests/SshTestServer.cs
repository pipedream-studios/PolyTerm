using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// The real SSH test server that <see cref="SshAutoReconnectTests"/>,
/// <see cref="RecordingRebindOnReconnectTests"/> and <see cref="SftpPermissionsTests"/> connect to.
/// It isn't something CI or every dev machine has, so it's configured only through environment
/// variables, and none of its details are committed to the repo:
/// <list type="bullet">
/// <item><c>POLYTERM_TEST_SSH_HOST</c>, <c>POLYTERM_TEST_SSH_USERNAME</c>: the server and the account
/// to log in as;</item>
/// <item><c>POLYTERM_TEST_SSH_KEY</c>: path to a dedicated, disposable private key whose public half
/// is in that account's <c>~/.ssh/authorized_keys</c>;</item>
/// <item><c>POLYTERM_TEST_SSH_REAL_VAULT_PROFILE_ID</c>: the ID of a profile for the same server in
/// the developer's real (non-isolated) vault, used only to run commands on the server from a separate
/// process (e.g. <c>sudo pkill</c> to break the connection) - not the profile the test connects with.</item>
/// </list>
/// When any of them is missing, <see cref="SkipUnlessConfigured"/> marks the test Skipped with the
/// reason (issue #218), rather than letting it return early and report Passed.
/// </summary>
internal static class SshTestServer
{
    public static string? Host { get; } = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_HOST");
    public static string? Username { get; } = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_USERNAME");
    public static string? KeyPath { get; } = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_KEY");
    public static string? RealVaultProfileId { get; } = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_REAL_VAULT_PROFILE_ID");

    /// <summary>Skips the calling test unless the whole SSH test server setup is configured.</summary>
    public static void SkipUnlessConfigured()
    {
        var missing = new List<string>();
        if (string.IsNullOrEmpty(Host)) missing.Add("POLYTERM_TEST_SSH_HOST");
        if (string.IsNullOrEmpty(Username)) missing.Add("POLYTERM_TEST_SSH_USERNAME");
        if (string.IsNullOrEmpty(KeyPath)) missing.Add("POLYTERM_TEST_SSH_KEY");
        if (string.IsNullOrEmpty(RealVaultProfileId)) missing.Add("POLYTERM_TEST_SSH_REAL_VAULT_PROFILE_ID");

        if (missing.Count > 0)
            Assert.Skip($"Needs a real SSH test server; not set: {string.Join(", ", missing)}. See {nameof(SshTestServer)}'s doc comment.");

        if (!File.Exists(KeyPath))
            Assert.Skip($"Needs a real SSH test server; the key POLYTERM_TEST_SSH_KEY points at doesn't exist: '{KeyPath}'.");
    }
}
