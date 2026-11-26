using AwesomeAssertions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Engine.Tests;

/// <summary>
/// The pipe name the app and its clients derive on their own, which has to be per-user (issue #378) and
/// the same on both ends. The override tests change process-wide environment variables, so only this class
/// may touch them.
/// </summary>
public sealed class McpPipeTests
{
    [Fact]
    public void LinuxName_UsesASocketInTheUsersRuntimeDirectory()
    {
        var root = Directory.CreateTempSubdirectory("pt-run-");
        try
        {
            root.CreateSubdirectory("1000");

            McpPipe.LinuxName(1000, root.FullName).Should().Be(Path.Combine(root.FullName, "1000", "polyterm.sock"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void LinuxName_WithoutARuntimeDirectory_FallsBackToANameWithTheUserId()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), $"pt-none-{Guid.NewGuid():N}");

        McpPipe.LinuxName(1000, missingRoot).Should().Be("polyterm-1000");
        McpPipe.LinuxName(1001, missingRoot).Should().Be("polyterm-1001");
    }

    [Fact]
    public void ParseEffectiveUserId_TakesTheSecondIdOnTheUidLine()
    {
        string[] status = ["Name:\tPolyTerm", "Uid:\t1000\t1001\t1002\t1003", "Gid:\t5\t6\t7\t8"];

        McpPipe.ParseEffectiveUserId(status).Should().Be(1001);
    }

    [Fact]
    public void DefaultName_IsPerUserWhereThePlatformSharesPipeNames()
    {
        var name = McpPipe.DefaultName();

        if (OperatingSystem.IsWindows())
            name.Should().StartWith("polyterm-S-1-");
        else if (OperatingSystem.IsLinux())
            name.Should().MatchRegex(@"^(/run/user/\d+/polyterm\.sock|polyterm-\d+)$");
        else
            name.Should().Be("polyterm");
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("", "", null, null)]
    [InlineData(null, "shared", "shared", "shared")]
    [InlineData("app-only", null, "app-only", null)]
    [InlineData("app-only", "shared", "app-only", "shared")]
    public void Overrides_AppVariableAppliesToTheAppAndClientVariableToBoth(
        string? appVariable, string? clientVariable, string? expectedForApp, string? expectedForClient)
    {
        var previousApp = Environment.GetEnvironmentVariable(McpPipe.AppVariable);
        var previousClient = Environment.GetEnvironmentVariable(McpPipe.ClientVariable);
        try
        {
            Environment.SetEnvironmentVariable(McpPipe.AppVariable, appVariable);
            Environment.SetEnvironmentVariable(McpPipe.ClientVariable, clientVariable);

            McpPipe.ResolveForApp().Should().Be(expectedForApp ?? McpPipe.DefaultName());
            McpPipe.ResolveForClient().Should().Be(expectedForClient ?? McpPipe.DefaultName());
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpPipe.AppVariable, previousApp);
            Environment.SetEnvironmentVariable(McpPipe.ClientVariable, previousClient);
        }
    }
}
