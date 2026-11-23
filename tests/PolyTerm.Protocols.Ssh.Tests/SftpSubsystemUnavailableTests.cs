using AwesomeAssertions;
using Renci.SshNet.Common;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>
/// Covers <see cref="SshSession.IsSftpSubsystemUnavailable"/>, which decides whether a failed SFTP
/// connect is the expected "host doesn't offer SFTP" case (logged quietly) or a real fault (logged as
/// a warning with the stack). Pure unit test, so unlike <see cref="SshSessionTests"/> it needs no sshd.
/// </summary>
public sealed class SftpSubsystemUnavailableTests
{
    [Fact]
    public void TheSshNetSubsystemRefusal_IsTheExpectedCase() =>
        SshSession.IsSftpSubsystemUnavailable(new SshException("Subsystem 'sftp' could not be executed."))
            .Should().BeTrue();

    [Theory]
    [InlineData("Subsystem 'netconf' could not be executed.")]
    [InlineData("Channel was closed.")]
    public void OtherSshExceptions_AreUnexpected(string message) =>
        SshSession.IsSftpSubsystemUnavailable(new SshException(message)).Should().BeFalse();

    [Fact]
    public void SshExceptionSubclasses_AreUnexpected_EvenWithTheSameMessage() =>
        SshSession.IsSftpSubsystemUnavailable(new SshConnectionException("Subsystem 'sftp' could not be executed."))
            .Should().BeFalse("a dropped connection during the SFTP handshake is a real fault");

    [Fact]
    public void NonSshExceptions_AreUnexpected() =>
        SshSession.IsSftpSubsystemUnavailable(new InvalidOperationException("Subsystem 'sftp' could not be executed."))
            .Should().BeFalse();
}
