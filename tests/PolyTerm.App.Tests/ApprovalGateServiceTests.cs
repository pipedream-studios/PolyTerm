using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <c>ApprovalGateService.ResolvePolicy</c> directly (bumped from private to internal for
/// exactly this purpose) — the "per-connection setting wins, else global default" resolution logic,
/// without touching the actual <c>ConfirmDialog</c> prompt (that part is genuine UI, see docs/testing.md).
/// Mutates the real <see cref="App.Settings"/> singleton to control the "global default" side of the
/// comparison — in the <c>AppSettings</c> xUnit collection (see <see cref="AppSettingsTestCollection"/>)
/// along with every other test class that touches <c>App.Settings</c>, so they never run concurrently
/// with each other and can't observe one another's transient mutations.
/// </summary>
[Collection("AppSettings")]
public sealed class ApprovalGateServiceTests
{
    private static ConnectionProfile ProfileWithPolicy(string? mcpPolicy)
    {
        var settings = new Dictionary<string, JsonElement>();
        if (mcpPolicy is not null)
            settings["mcpPolicy"] = JsonSerializer.SerializeToElement(mcpPolicy);
        return new ConnectionProfile { Name = "Test", ProtocolId = "ssh", Settings = settings };
    }

    [Theory]
    [InlineData("Allow")]
    [InlineData("Deny")]
    [InlineData("Prompt")]
    public void PerConnectionPolicy_Wins_RegardlessOfGlobalDefault(string perConnectionPolicy)
    {
        App.Settings.McpApprovalPolicy = perConnectionPolicy == "Allow" ? "Deny" : "Allow"; // deliberately opposite
        var profile = ProfileWithPolicy(perConnectionPolicy);

        ApprovalGateService.ResolvePolicy(profile).Should().Be(perConnectionPolicy);
    }

    [Fact]
    public void NoPerConnectionPolicy_FallsBackToGlobalDefault()
    {
        App.Settings.McpApprovalPolicy = "Allow";
        var profile = ProfileWithPolicy(null);

        ApprovalGateService.ResolvePolicy(profile).Should().Be("Allow");
    }

    [Theory]
    [InlineData("Inherit")] // what the New/Edit Connection dialog omits rather than ever actually writes
    [InlineData("Bogus")]
    [InlineData("")]
    public void UnrecognizedPerConnectionValue_FallsBackToGlobalDefault(string unrecognizedValue)
    {
        App.Settings.McpApprovalPolicy = "Deny";
        var profile = ProfileWithPolicy(unrecognizedValue);

        ApprovalGateService.ResolvePolicy(profile).Should().Be("Deny");
    }

    [Fact]
    public void WrongJsonValueKind_FallsBackToGlobalDefault()
    {
        App.Settings.McpApprovalPolicy = "Prompt";
        var profile = new ConnectionProfile
        {
            Name = "Test", ProtocolId = "ssh",
            Settings = new Dictionary<string, JsonElement> { ["mcpPolicy"] = JsonSerializer.SerializeToElement(42) },
        };

        ApprovalGateService.ResolvePolicy(profile).Should().Be("Prompt");
    }

    [Theory]
    [InlineData("Allow", true)]
    [InlineData("Deny", false)]
    public async Task CheckAsync_UnderAnAllowOrDenyPolicy_DecidesByPolicy_WithoutPrompting(string policy, bool allowed)
    {
        var gate = new ApprovalGateService(NullLogger<ApprovalGateService>.Instance);

        var decision = await gate.CheckAsync(McpAction.TerminalExecute, ProfileWithPolicy(policy), "ls", TestContext.Current.CancellationToken);

        decision.Should().Be(new ApprovalDecision(allowed, policy, Prompted: false));
        decision.DecidedBy.Should().Be("policy");
    }

    [Fact]
    public void ApprovalDecision_ReportsWhoDecided_AndWhyItWasDenied()
    {
        ApprovalDecision.ByUser(true).DecidedBy.Should().Be("user");
        ApprovalDecision.ByPolicy("Allow").DecidedBy.Should().Be("policy");

        ApprovalDecision.ByUser(false).DenialReason.Should().Contain("rejected it at the approval prompt");
        ApprovalDecision.ByPolicy("Deny").DenialReason.Should().Contain("policy is Deny");
        ApprovalDecision.NoWindowToPrompt.Should().Be(new ApprovalDecision(false, "Prompt", Prompted: false));
        ApprovalDecision.NoWindowToPrompt.DenialReason.Should().Contain("no main window").And.Contain("fail closed");
    }

    [Fact]
    public void ApprovalDecision_ToResponse_SerializesAsPolicyAndDecidedBy()
    {
        JsonSerializer.Serialize(ApprovalDecision.ByUser(true).ToResponse())
            .Should().Be("""{"policy":"Prompt","decided_by":"user"}""");
        JsonSerializer.Serialize(ApprovalDecision.ByPolicy("Allow").ToResponse())
            .Should().Be("""{"policy":"Allow","decided_by":"policy"}""");
    }
}
