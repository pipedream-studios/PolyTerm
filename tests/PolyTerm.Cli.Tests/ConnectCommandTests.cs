using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PolyTerm.Cli.Commands;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Cli.Tests;

/// <summary>
/// Covers the connect-failure crash fix: <c>ConnectCommand</c> used to have no top-level try/catch
/// around <c>SessionManager.OpenAsync</c>, so any protocol's connect failure fell through to
/// System.CommandLine's own default exception-handler dump instead of a clean message. Deliberately
/// does NOT test the connect-success path — that spawns a background task reading real process stdin
/// (<c>Console.OpenStandardInput()</c>), which would hang an automated test process rather than fail
/// cleanly; the success path was verified manually/live instead (see docs/todos.md).
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ConnectCommandTests
{
    private static IServiceProvider MakeServices(ISessionManager sessionManager) =>
        new ServiceCollection().AddSingleton(sessionManager).BuildServiceProvider();

    [Fact]
    public async Task ConnectFailure_PrintsACleanMessage_AndReturnsExitCode1_NotAnUnhandledExceptionDump()
    {
        var sessionManager = new Mock<ISessionManager>();
        var badId = Guid.NewGuid();
        sessionManager.Setup(s => s.OpenAsync(badId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"Profile {badId} not found."));

        var command = ConnectCommand.Build(MakeServices(sessionManager.Object));

        var (exitCode, _, error) = await ConsoleCapture.RunAsync(command, badId.ToString());

        exitCode.Should().Be(1);
        error.Should().Contain("Connect failed:").And.Contain($"Profile {badId} not found.");
        error.Should().NotContain("at PolyTerm", "a clean one-line message should replace the stack-trace dump, not sit alongside it");
    }

    [Fact]
    public async Task ConnectFailure_WithADifferentExceptionType_StillProducesACleanMessage()
    {
        // The fix catches Exception broadly (a CLI top-level boundary, matching the guidance that
        // system-boundary code may catch broadly) — confirm it's not accidentally narrowed to one type.
        var sessionManager = new Mock<ISessionManager>();
        var id = Guid.NewGuid();
        sessionManager.Setup(s => s.OpenAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Connection timed out."));

        var command = ConnectCommand.Build(MakeServices(sessionManager.Object));

        var (exitCode, _, error) = await ConsoleCapture.RunAsync(command, id.ToString());

        exitCode.Should().Be(1);
        error.Should().Contain("Connect failed: Connection timed out.");
    }
}
