using System.Text.Json;
using AwesomeAssertions;
using PolyTerm.App.McpBridge;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>Covers <see cref="McpAuditLogger"/>'s append-only JSON-line writes against a private temp file (never the user's real <c>mcp-audit.log</c>).</summary>
public sealed class McpAuditLoggerTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"polyterm-test-audit-{Guid.NewGuid():N}.log");

    [Fact]
    public void Log_AppendsAJsonLine_WithTheGivenFields()
    {
        var path = TempPath();
        try
        {
            var audit = new McpAuditLogger(path);

            audit.Log("terminal_execute", "abc-123", "my-server", "{command=\"ls\"}", success: true);

            var line = File.ReadAllLines(path).Should().ContainSingle().Subject;
            using var doc = JsonDocument.Parse(line);
            doc.RootElement.GetProperty("tool").GetString().Should().Be("terminal_execute");
            doc.RootElement.GetProperty("session_id").GetString().Should().Be("abc-123");
            doc.RootElement.GetProperty("profile").GetString().Should().Be("my-server");
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Log_RecordsTheErrorMessage_OnFailure()
    {
        var path = TempPath();
        try
        {
            var audit = new McpAuditLogger(path);

            audit.Log("connection_open", null, null, "{name=\"prod\"}", success: false, error: "Denied by MCP approval policy.");

            using var doc = JsonDocument.Parse(File.ReadAllLines(path).Single());
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("error").GetString().Should().Be("Denied by MCP approval policy.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Log_AppendsMultipleEntries_OnePerLine()
    {
        var path = TempPath();
        try
        {
            var audit = new McpAuditLogger(path);

            audit.Log("session_list", null, null, "{}", success: true);
            audit.Log("session_list", null, null, "{}", success: true);
            audit.Log("session_list", null, null, "{}", success: true);

            File.ReadAllLines(path).Should().HaveCount(3);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Log_NeverThrows_WhenThePathIsUnwritable()
    {
        // An intentionally invalid path (a directory that doesn't exist and won't be created) —
        // Log() must swallow the failure rather than let a broken audit sink break the tool call
        // it's meant to be recording.
        var badPath = Path.Combine(TempPath() + "-does-not-exist-dir", "audit.log");
        var audit = new McpAuditLogger(badPath);

        var act = () => audit.Log("session_list", null, null, "{}", success: true);

        act.Should().NotThrow();
    }
}
