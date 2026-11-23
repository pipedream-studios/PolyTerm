using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace PolyTerm.Protocols.RdpFreerdp.Tests;

/// <summary>How an RDP profile's saved settings become the shim's drive shares (issue #269).</summary>
public sealed class RdpDriveSharesTests
{
    private static Dictionary<string, JsonElement> Settings(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void FromSettings_NoDriveSettings_ReturnsNothing()
    {
        RdpDriveShares.FromSettings(Settings("""{ "domain": "CORP" }""")).Should().BeEmpty();
    }

    [Fact]
    public void FromSettings_LegacyChecklist_SharesEachDriveUnderADerivedName()
    {
        var shares = RdpDriveShares.FromSettings(Settings(
            """{ "redirectDrives": true, "redirectedDrives": ["C:\\", "d:\\", "/", "/Volumes/Data"] }"""));

        shares.Should().Equal(
            new RdpDriveShare("C", "C:\\"),
            new RdpDriveShare("D", "d:\\"),
            new RdpDriveShare("root", "/"),
            new RdpDriveShare("Data", "/Volumes/Data"));
    }

    [Fact]
    public void FromSettings_RedirectDrivesFalse_IgnoresAStaleList()
    {
        RdpDriveShares.FromSettings(Settings(
            """{ "redirectDrives": false, "redirectedDrives": ["C:\\"] }""")).Should().BeEmpty();
    }

    [Fact]
    public void FromSettings_DriveShares_UsesNamesAsGivenAndTakesPrecedenceOverTheLegacyList()
    {
        var shares = RdpDriveShares.FromSettings(Settings("""
            {
              "redirectDrives": true,
              "redirectedDrives": ["C:\\"],
              "driveShares": [
                { "name": "Projects", "path": "/Users/me/Projects" },
                { "name": "", "path": "/Users/me/Downloads/" },
                { "name": "NoPath" },
                "not-an-object"
              ]
            }
            """));

        shares.Should().Equal(
            new RdpDriveShare("Projects", "/Users/me/Projects"),
            new RdpDriveShare("Downloads", "/Users/me/Downloads/"));
    }

    [Fact]
    public void FromSettings_DuplicateNames_KeepsTheFirst()
    {
        var shares = RdpDriveShares.FromSettings(Settings("""
            { "driveShares": [ { "name": "src", "path": "/a/src" }, { "name": "SRC", "path": "/b/src" } ] }
            """));

        shares.Should().Equal(new RdpDriveShare("src", "/a/src"));
    }

    [Theory]
    [InlineData("C:\\", "C")]
    [InlineData("C:", "C")]
    [InlineData("C:\\Users\\me", "me")]
    [InlineData("/", "root")]
    [InlineData("/home/me/", "me")]
    [InlineData("share", "share")]
    public void NameFromPath(string path, string expected)
    {
        RdpDriveShares.NameFromPath(path).Should().Be(expected);
    }
}
