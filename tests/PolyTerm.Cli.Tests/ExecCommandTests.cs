using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PolyTerm.Cli.Commands;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Cli.Tests;

/// <summary>Same connect-failure crash fix as <c>ConnectCommandTests</c>, applied to <c>ExecCommand</c> (a separate command, separate fix site).</summary>
[Collection("ConsoleRedirect")]
public sealed class ExecCommandTests
{
    private static IServiceProvider MakeServices(ISessionManager sessionManager) =>
        new ServiceCollection().AddSingleton(sessionManager).BuildServiceProvider();

    [Fact]
    public async Task ConnectFailure_PrintsACleanMessage_AndReturnsExitCode1()
    {
        var sessionManager = new Mock<ISessionManager>();
        var badId = Guid.NewGuid();
        sessionManager.Setup(s => s.OpenAsync(badId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"Profile {badId} not found."));

        var command = ExecCommand.Build(MakeServices(sessionManager.Object));

        var (exitCode, _, error) = await ConsoleCapture.RunAsync(command, badId.ToString(), "echo hi");

        exitCode.Should().Be(1);
        error.Should().Contain("Connect failed:").And.Contain($"Profile {badId} not found.");
    }
}
