using System.IO;
using PolyTerm.App.McpBridge;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <c>McpBridgeService.DescribePipeEndpoint</c>, the path the startup log reports (issue #279):
/// the real Windows pipe path on Windows, the CoreFxPipe Unix domain socket elsewhere.
/// </summary>
public sealed class McpBridgeServiceEndpointTests
{
    [Fact]
    public void DescribePipeEndpoint_ReportsPlatformEndpoint()
    {
        var endpoint = McpBridgeService.DescribePipeEndpoint("polyterm");

        if (OperatingSystem.IsWindows())
            Assert.Equal(@"\\.\pipe\polyterm", endpoint);
        else
            Assert.Equal(Path.Combine(Path.GetTempPath(), "CoreFxPipe_polyterm"), endpoint);
    }
}
