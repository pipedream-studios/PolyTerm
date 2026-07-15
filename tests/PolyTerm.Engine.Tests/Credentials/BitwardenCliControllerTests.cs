using AwesomeAssertions;
using PolyTerm.Engine.Credentials;
using Xunit;

namespace PolyTerm.Engine.Tests.Credentials;

/// <summary>
/// Covers <see cref="BitwardenCliController.ParseStatus"/>, the pure JSON-parsing piece of the issue
/// #78 login/unlock flow. The rest of <c>BitwardenCliController</c> shells out to the real <c>bw</c>
/// CLI and starts a <c>bw serve</c> child process, which isn't something this suite can exercise
/// without the CLI installed - see docs/testing.md for the project's stance on that class of test.
/// </summary>
public sealed class BitwardenCliControllerTests
{
    [Theory]
    [InlineData("""{"status":"unlocked"}""", BitwardenAuthStatus.Unlocked)]
    [InlineData("""{"status":"locked"}""", BitwardenAuthStatus.Locked)]
    [InlineData("""{"status":"unauthenticated"}""", BitwardenAuthStatus.Unauthenticated)]
    [InlineData("""{"status":"something-new-bw-cli-invented"}""", BitwardenAuthStatus.Unknown)]
    [InlineData("""{"noStatusField":true}""", BitwardenAuthStatus.Unknown)]
    [InlineData("not json at all", BitwardenAuthStatus.Unknown)]
    [InlineData("", BitwardenAuthStatus.Unknown)]
    [InlineData("   ", BitwardenAuthStatus.Unknown)]
    public void ParseStatus_MapsBwCliJsonToAuthStatus(string stdout, BitwardenAuthStatus expected)
    {
        BitwardenCliController.ParseStatus(stdout).Should().Be(expected);
    }

    [Fact]
    public void ParseStatus_RealBwStatusShape_ParsesAsUnlocked()
    {
        // Actual shape of `bw status`'s stdout: a flat object, not nested under "data"/"template" the
        // way bw serve's HTTP /status response is (see BitwardenProvider.IsAvailableAsync) - these are
        // two different surfaces of the same CLI and deliberately not the same JSON shape.
        const string stdout = """
            {"serverUrl":"https://vault.bitwarden.com","lastSync":"2026-07-16T00:00:00.000Z","userEmail":"user@example.com","userId":"11111111-2222-3333-4444-555555555555","status":"unlocked"}
            """;

        BitwardenCliController.ParseStatus(stdout).Should().Be(BitwardenAuthStatus.Unlocked);
    }
}
