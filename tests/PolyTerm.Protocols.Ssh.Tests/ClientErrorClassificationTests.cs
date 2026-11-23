using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;
using Xunit;
using static PolyTerm.Protocols.Ssh.SshSession;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>
/// The decision <see cref="SshSession"/> makes when SSH.NET reports a connection error (issue #264).
/// Pure logic, so unlike <see cref="SshSessionTests"/> these need no real sshd and run on every platform.
/// </summary>
public sealed class ClientErrorClassificationTests
{
    [Fact]
    public void ShellEnded_WhileConnected_ClosesAtInformation_AndDoesNotReconnect()
    {
        ClassifyClientError(SessionState.Connected, shellEnded: true)
            .Should().Be((ClientErrorAction.CloseShellEnded, LogLevel.Information));
    }

    [Fact]
    public void ConnectionDropped_WithShellStillOpen_ReconnectsAtWarning()
    {
        ClassifyClientError(SessionState.Connected, shellEnded: false)
            .Should().Be((ClientErrorAction.Reconnect, LogLevel.Warning));
    }

    [Theory]
    [InlineData(SessionState.Closing, false)]
    [InlineData(SessionState.Closing, true)]
    [InlineData(SessionState.Closed, false)]
    [InlineData(SessionState.Reconnecting, false)]
    [InlineData(SessionState.Reconnecting, true)]
    [InlineData(SessionState.Connecting, false)]
    [InlineData(SessionState.Faulted, false)]
    public void ErrorOutsideConnected_IsIgnoredAtDebug(SessionState state, bool shellEnded)
    {
        ClassifyClientError(state, shellEnded)
            .Should().Be((ClientErrorAction.Ignore, LogLevel.Debug));
    }
}
