using System.ComponentModel;
using AwesomeAssertions;
using PolyTerm.Engine.Credentials;
using Xunit;

namespace PolyTerm.Engine.Tests.Credentials;

/// <summary>
/// What happens when <c>secret-tool</c> can't be started (issue #367), by pointing
/// <see cref="LinuxSecretService"/> at a program that doesn't exist. No keyring is needed, so these
/// run on every OS.
/// </summary>
public sealed class LinuxSecretServiceTests
{
    // Scoped to the running test's async flow, and gone when it ends - no cleanup needed.
    private static void UseMissingExecutable() =>
        LinuxSecretService.ExecutableOverride.Value = $"polyterm-no-such-tool-{Guid.NewGuid():N}";

    private static async Task AssertSaysWhatToInstall(Func<Task> act)
    {
        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain("secret-tool").And.Contain("libsecret-tools");
        assertion.Which.InnerException.Should().BeOfType<Win32Exception>();
    }

    [Fact]
    public async Task WriteAsync_WhenTheToolIsMissing_SaysWhatToInstall()
    {
        UseMissingExecutable();

        await AssertSaysWhatToInstall(() =>
            LinuxSecretService.WriteAsync("target", "alice", "secret", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_WhenTheToolIsMissing_SaysWhatToInstall()
    {
        UseMissingExecutable();

        await AssertSaysWhatToInstall(() =>
            LinuxSecretService.ReadAsync("target", "alice", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearAsync_WhenTheToolIsMissing_SaysWhatToInstall()
    {
        UseMissingExecutable();

        await AssertSaysWhatToInstall(() =>
            LinuxSecretService.ClearAsync("target", TestContext.Current.CancellationToken));
    }
}
