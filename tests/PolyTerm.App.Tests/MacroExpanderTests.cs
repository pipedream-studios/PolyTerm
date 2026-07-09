using System.Text.Json;
using FluentAssertions;
using PolyTerm.App.Macros;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

public sealed class MacroExpanderTests
{
    private static ConnectionProfile MakeProfile(string? host = "10.0.0.1", int? port = 22,
        string name = "My Server", string? username = "alice")
    {
        var settings = new Dictionary<string, JsonElement>();
        if (username is not null)
            settings["username"] = JsonSerializer.SerializeToElement(username);

        return new ConnectionProfile { Name = name, ProtocolId = "ssh", Host = host, Port = port, Settings = settings };
    }

    [Fact]
    public void Expand_SubstitutesAllKnownVariables()
    {
        var profile = MakeProfile();

        var result = MacroExpander.Expand(
            "connect {{username}}@{{host}}:{{port}} ({{name}}) [{{sessionTitle}}]", profile, "bash");

        result.Should().Be("connect alice@10.0.0.1:22 (My Server) [bash]");
    }

    [Fact]
    public void Expand_Username_FallsBackToEnvironmentUserName_WhenSettingMissing()
    {
        var profile = MakeProfile(username: null);

        var result = MacroExpander.Expand("{{username}}", profile, null);

        result.Should().Be(Environment.UserName);
    }

    [Fact]
    public void Expand_HostAndPort_RenderAsEmptyString_WhenNull()
    {
        var profile = MakeProfile(host: null, port: null);

        var result = MacroExpander.Expand("[{{host}}:{{port}}]", profile, null);

        result.Should().Be("[:]");
    }

    /// <summary>
    /// Surprising, verified-by-reading-the-code behavior: with a null profile, ONLY {{sessionTitle}}
    /// gets replaced — {{host}}/{{port}}/{{name}}/{{username}} are left as literal, unexpanded text
    /// rather than becoming empty strings. Locking this in so it's a deliberate contract, not an
    /// accident that changes silently.
    /// </summary>
    [Fact]
    public void Expand_NullProfile_OnlyExpandsSessionTitle_LeavesOtherTokensLiteral()
    {
        var result = MacroExpander.Expand("{{host}} {{port}} {{name}} {{username}} {{sessionTitle}}", null, "my-tab");

        result.Should().Be("{{host}} {{port}} {{name}} {{username}} my-tab");
    }

    [Fact]
    public void Expand_NullProfile_NullSessionTitle_RendersEmptyForSessionTitle()
    {
        var result = MacroExpander.Expand("[{{sessionTitle}}]", null, null);

        result.Should().Be("[]");
    }

    [Fact]
    public void Expand_UnknownVariable_LeftUntouched()
    {
        var profile = MakeProfile();

        var result = MacroExpander.Expand("{{host}} {{notARealVariable}}", profile, null);

        result.Should().Be("10.0.0.1 {{notARealVariable}}");
    }

    [Fact]
    public void Expand_RepeatedVariable_ReplacesEveryOccurrence()
    {
        var profile = MakeProfile(host: "example.com");

        var result = MacroExpander.Expand("{{host}} {{host}} {{host}}", profile, null);

        result.Should().Be("example.com example.com example.com");
    }
}
