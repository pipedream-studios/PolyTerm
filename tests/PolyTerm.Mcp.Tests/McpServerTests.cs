using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace PolyTerm.Mcp.Tests;

/// <summary>
/// Covers <see cref="McpServer"/> end-to-end over real <see cref="TextReader"/>/<see cref="TextWriter"/>
/// streams (the same API it's actually driven with via stdin/stdout in <c>Program.cs</c>) against a
/// <see cref="FakePolyTermClient"/> — no real named pipe or PolyTerm.App instance needed. This is
/// PolyTerm.Mcp's first test coverage of any kind: the actual client-facing MCP protocol layer
/// (JSON-RPC framing, the tools/list schema, tools/call dispatch, image-vs-text content-block choice)
/// had zero automated tests before this, despite being the surface any real MCP client (Claude Code,
/// etc.) actually talks to.
/// </summary>
public sealed class McpServerTests
{
    private static async Task<string> RunOneRequestAsync(McpServer server, object request)
    {
        var input = JsonSerializer.Serialize(request) + "\n";
        using var stdin = new StringReader(input);
        using var stdout = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // RunAsync's loop exits naturally once ReadLineAsync returns null (StringReader is exhausted
        // after the one line) — no need for the token to actually fire for a well-behaved single request.
        await server.RunAsync(stdin, stdout, cts.Token);

        return stdout.ToString();
    }

    private static async Task<JsonDocument> RunAndParseAsync(McpServer server, object request)
    {
        var output = await RunOneRequestAsync(server, request);
        return JsonDocument.Parse(output.Trim());
    }

    [Fact]
    public async Task Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        var server = new McpServer(new FakePolyTermClient());

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "initialize" });

        doc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString().Should().Be("2024-11-05");
        doc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("PolyTerm");
    }

    [Fact]
    public async Task Ping_ReturnsEmptyResult()
    {
        var server = new McpServer(new FakePolyTermClient());

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "ping" });

        doc.RootElement.GetProperty("id").GetInt32().Should().Be(1);
        doc.RootElement.TryGetProperty("result", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ToolsList_IncludesEveryToolName_IncludingFramebufferTools()
    {
        var server = new McpServer(new FakePolyTermClient());

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "tools/list" });

        var names = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

        names.Should().Contain([
            "session_list", "connection_list", "connection_open", "connection_close",
            "terminal_read", "terminal_execute", "terminal_send_keys",
            "sftp_list", "sftp_read", "sftp_write", "sftp_delete", "sftp_rename",
            "framebuffer_screenshot", "framebuffer_click", "framebuffer_type", "framebuffer_key", "framebuffer_resize",
        ]);
    }

    [Fact]
    public async Task ToolsList_FramebufferClick_DeclaresXYAndButtonProperties()
    {
        var server = new McpServer(new FakePolyTermClient());

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "tools/list" });

        var click = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Single(t => t.GetProperty("name").GetString() == "framebuffer_click");

        var props = click.GetProperty("inputSchema").GetProperty("properties");
        props.TryGetProperty("x", out _).Should().BeTrue();
        props.TryGetProperty("y", out _).Should().BeTrue();
        props.TryGetProperty("button", out _).Should().BeTrue();
        props.GetProperty("x").GetProperty("type").GetString().Should().Be("integer");
    }

    [Fact]
    public async Task ToolsList_SessionTools_AdvertiseIdAliasAndRequireOnlyTheOtherParams()
    {
        var server = new McpServer(new FakePolyTermClient());

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "tools/list" });

        var exec = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Single(t => t.GetProperty("name").GetString() == "terminal_execute");

        var schema = exec.GetProperty("inputSchema");
        schema.GetProperty("properties").TryGetProperty("session_id", out _).Should().BeTrue();
        schema.GetProperty("properties").TryGetProperty("id", out _).Should().BeTrue();
        schema.GetProperty("required").EnumerateArray().Select(r => r.GetString())
            .Should().BeEquivalentTo(["command"]);
    }

    [Fact]
    public async Task ToolsCall_ForwardsMethodAndArguments_ToTheClient()
    {
        var client = new FakePolyTermClient();
        var server = new McpServer(client);

        await RunOneRequestAsync(server, new
        {
            id = 1,
            method = "tools/call",
            @params = new { name = "terminal_execute", arguments = new { session_id = "abc-123", command = "ls" } },
        });

        client.Calls.Should().ContainSingle();
        client.Calls[0].Method.Should().Be("terminal_execute");
        client.Calls[0].Args!["session_id"].GetString().Should().Be("abc-123");
        client.Calls[0].Args!["command"].GetString().Should().Be("ls");
    }

    [Fact]
    public async Task ToolsCall_ReturnsTextContent_WhenTheResultHasATextField()
    {
        var client = new FakePolyTermClient { OnCall = (_, _) => JsonDocument.Parse("""{"text":"hello world"}""").RootElement.Clone() };
        var server = new McpServer(client);

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "tools/call", @params = new { name = "terminal_read" } });

        var content = doc.RootElement.GetProperty("result").GetProperty("content")[0];
        content.GetProperty("type").GetString().Should().Be("text");
        content.GetProperty("text").GetString().Should().Be("hello world");
    }

    [Theory]
    [InlineData("""{"text":"ls: cannot access 'x'","exit_code":2,"timed_out":false,"shell":"posix"}""", "ls: cannot access 'x'\n[exit code 2]")]
    [InlineData("""{"text":"","exit_code":0,"timed_out":false,"shell":"posix"}""", "[exit code 0]")]
    [InlineData("""{"text":"[sudo] password for rob: ","exit_code":null,"timed_out":true,"shell":"posix"}""",
                "[sudo] password for rob: \n[timed out - the command may still be running or waiting for input; see terminal_read / terminal_send_keys]")]
    [InlineData("""{"text":"router# ","exit_code":null,"timed_out":false,"shell":"unknown"}""", "router# ")]
    public async Task ToolsCall_TerminalExecute_AppendsTheExitCodeOrTimeoutToTheText(string json, string expected)
    {
        var client = new FakePolyTermClient { OnCall = (_, _) => JsonDocument.Parse(json).RootElement.Clone() };
        var server = new McpServer(client);

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "tools/call", @params = new { name = "terminal_execute" } });

        doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task ToolsCall_FallsBackToRawJson_WhenTheResultHasNoTextField()
    {
        var client = new FakePolyTermClient { OnCall = (_, _) => JsonDocument.Parse("""{"ok":true,"path":"/etc"}""").RootElement.Clone() };
        var server = new McpServer(client);

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "tools/call", @params = new { name = "sftp_delete" } });

        var content = doc.RootElement.GetProperty("result").GetProperty("content")[0];
        content.GetProperty("type").GetString().Should().Be("text");
        content.GetProperty("text").GetString().Should().Contain("\"ok\":true");
    }

    [Fact]
    public async Task ToolsCall_ReturnsAnImageContentBlock_ForFramebufferScreenshot()
    {
        const string fakeBase64 = "aGVsbG8="; // "hello", doesn't need to be a real PNG for this test
        var client = new FakePolyTermClient
        {
            OnCall = (_, _) => JsonDocument.Parse($$"""{"image_base64":"{{fakeBase64}}","width":100,"height":50}""").RootElement.Clone(),
        };
        var server = new McpServer(client);

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "tools/call", @params = new { name = "framebuffer_screenshot" } });

        var content = doc.RootElement.GetProperty("result").GetProperty("content")[0];
        content.GetProperty("type").GetString().Should().Be("image");
        content.GetProperty("data").GetString().Should().Be(fakeBase64);
        content.GetProperty("mimeType").GetString().Should().Be("image/png");
    }

    [Fact]
    public async Task Notification_WithNoId_ProducesNoResponse()
    {
        var server = new McpServer(new FakePolyTermClient());

        var output = await RunOneRequestAsync(server, new { method = "ping" }); // no "id" — a notification

        output.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownMethod_ReturnsAJsonRpcError_NotACrash()
    {
        var server = new McpServer(new FakePolyTermClient());

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "not_a_real_method" });

        doc.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Contain("Method not found");
    }

    [Fact]
    public async Task ClientThrows_ReturnsAJsonRpcError_NotACrash()
    {
        var client = new FakePolyTermClient { ThrowOnCall = new InvalidOperationException("Denied by MCP approval policy.") };
        var server = new McpServer(client);

        using var doc = await RunAndParseAsync(server, new { id = 1, method = "tools/call", @params = new { name = "connection_open" } });

        doc.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be("Denied by MCP approval policy.");
    }

    [Fact]
    public async Task MalformedJson_WithNoRecoverableId_ProducesNoResponse()
    {
        var server = new McpServer(new FakePolyTermClient());
        using var stdin = new StringReader("this is not { json at all\n");
        using var stdout = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await server.RunAsync(stdin, stdout, cts.Token);

        stdout.ToString().Should().BeEmpty("there's no id to reply to in genuinely unparseable input");
    }
}
