using System.Globalization;
using System.Text;
using FluentAssertions;
using PolyTerm.App.Import;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

public sealed class MobaXtermImporterTests
{
    // MobaXterm's real layout is " #<icon>#<type>%<host>%<port>%<username>%<...filler fields>",
    // with a leading space before the first '#' — this helper builds a syntactically valid session
    // value without hand-typing the exact filler fields (-1/%%/etc.) each time.
    private static string SessionValue(int typeCode, string host, int? port = null, string? username = null) =>
        $" #109#{typeCode}%{host}%{port?.ToString(CultureInfo.InvariantCulture) ?? ""}%{username ?? ""}%-1%-1%%-1%0%%-1%%0# %-1#-1";

    private static ImportResult ImportContent(string content) =>
        MobaXtermImporter.Import(new MemoryStream(Encoding.Latin1.GetBytes(content)));

    [Fact]
    public void Import_RootLevelSshSession_MapsCorrectly()
    {
        var content = $"""
            [Bookmarks]
            SubRep=
            ImgNum=41
            MyServer={SessionValue(0, "192.168.1.10", 22, "root")}
            """;

        var result = ImportContent(content);

        result.Profiles.Should().ContainSingle();
        var profile = result.Profiles[0];
        profile.Name.Should().Be("MyServer");
        profile.ProtocolId.Should().Be("ssh");
        profile.Host.Should().Be("192.168.1.10");
        profile.Port.Should().Be(22);
        profile.ParentId.Should().BeNull();
        profile.Settings.Should().ContainKey("username")
            .WhoseValue.GetString().Should().Be("root");
    }

    [Theory]
    [InlineData(1, "telnet")]
    [InlineData(4, "rdp")]
    [InlineData(5, "vnc")]
    [InlineData(7, "ssh")] // SFTP bookmarks map to plain SSH
    [InlineData(11, "https")]
    public void Import_MapsSessionTypeCode_ToExpectedProtocol(int typeCode, string expectedProtocol)
    {
        var content = $"""
            [Bookmarks]
            SubRep=
            Session1={SessionValue(typeCode, "host.example.com")}
            """;

        var result = ImportContent(content);

        result.Profiles.Should().ContainSingle();
        result.Profiles[0].ProtocolId.Should().Be(expectedProtocol);
    }

    [Fact]
    public void Import_UnsupportedSessionType_IsSkippedWithReason()
    {
        // Type 2 = Rsh, which has no equivalent protocol module in this app.
        var content = $"""
            [Bookmarks]
            SubRep=
            RshBox={SessionValue(2, "host.example.com")}
            """;

        var result = ImportContent(content);

        result.Profiles.Should().BeEmpty();
        result.Skipped.Should().ContainSingle(s => s.Contains("RshBox") && s.Contains("unsupported session type"));
    }

    [Fact]
    public void Import_MalformedValue_IsSkippedAsUnrecognizedFormat()
    {
        var content = """
            [Bookmarks]
            SubRep=
            Broken=notEnoughHashSigns
            """;

        var result = ImportContent(content);

        result.Profiles.Should().BeEmpty();
        result.Skipped.Should().ContainSingle(s => s.Contains("Broken") && s.Contains("unrecognized format"));
    }

    [Fact]
    public void Import_MissingPort_FallsBackToProtocolDefault()
    {
        var content = $"""
            [Bookmarks]
            SubRep=
            NoPort={SessionValue(4, "rdp-host.example.com", port: null)}
            """;

        var result = ImportContent(content);

        result.Profiles.Should().ContainSingle();
        result.Profiles[0].Port.Should().Be(3389); // RDP default
    }

    [Fact]
    public void Import_SingleLevelFolder_CreatesFolderAndAssignsParent()
    {
        var content = $"""
            [Bookmarks_1]
            SubRep=Servers
            ProdServer={SessionValue(0, "10.0.0.5", 22, "admin")}
            """;

        var result = ImportContent(content);

        var folder = result.Profiles.Should().ContainSingle(p => p.IsFolder).Subject;
        folder.Name.Should().Be("Servers");
        folder.ParentId.Should().BeNull();

        var session = result.Profiles.Should().ContainSingle(p => !p.IsFolder).Subject;
        session.ParentId.Should().Be(folder.Id);
    }

    [Fact]
    public void Import_NestedFolderPath_CreatesFullChain_ParentsBeforeChildren()
    {
        var content = $"""
            [Bookmarks_1]
            SubRep=Servers\Prod
            ProdServer={SessionValue(0, "10.0.0.5", 22, "admin")}
            """;

        var result = ImportContent(content);

        var folders = result.Profiles.Where(p => p.IsFolder).ToList();
        folders.Should().HaveCount(2);

        var outer = folders.Single(f => f.Name == "Servers");
        var inner = folders.Single(f => f.Name == "Prod");
        outer.ParentId.Should().BeNull();
        inner.ParentId.Should().Be(outer.Id);

        // Depth-first: parents must appear before their children in the list, since SaveAsync
        // uses ParentId as a foreign-key reference.
        var order = result.Profiles.ToList();
        order.IndexOf(outer).Should().BeLessThan(order.IndexOf(inner));

        var session = result.Profiles.Single(p => !p.IsFolder);
        session.ParentId.Should().Be(inner.Id);
    }

    [Fact]
    public void Import_TwoSessionsInSameFolder_ReuseTheSameFolderProfile()
    {
        var content = $"""
            [Bookmarks_1]
            SubRep=Servers
            Server1={SessionValue(0, "10.0.0.1", 22, "admin")}
            Server2={SessionValue(0, "10.0.0.2", 22, "admin")}
            """;

        var result = ImportContent(content);

        result.Profiles.Should().ContainSingle(p => p.IsFolder);
        result.Profiles.Where(p => !p.IsFolder).Should().HaveCount(2);
    }

    [Fact]
    public void Import_NeverImportsPasswordData()
    {
        // No password field exists anywhere in the parsed shape/Settings — this documents that
        // contract rather than testing a specific negative string, since there's no password input
        // to the parser at all in MobaXterm's own session-line format for this app to accidentally read.
        var content = $"""
            [Bookmarks]
            SubRep=
            MyServer={SessionValue(0, "192.168.1.10", 22, "root")}
            """;

        var result = ImportContent(content);

        result.Profiles[0].Credential.Should().BeNull();
        result.Profiles[0].Settings.Should().NotContainKey("password");
    }

    [Fact]
    public void Import_NonBookmarksSections_AreIgnored()
    {
        var content = """
            [General]
            SomeKey=SomeValue

            [Bookmarks]
            SubRep=
            """;

        var result = ImportContent(content);

        result.Profiles.Should().BeEmpty();
        result.Skipped.Should().BeEmpty();
    }
}
