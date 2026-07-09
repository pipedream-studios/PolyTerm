using System.Text.Json;

namespace PolyTerm.App.McpBridge;

/// <summary>
/// Structured, append-only audit trail for every MCP tool call (spec §4.7 design rule 5: "timestamp,
/// tool, profile, result"), written as JSON lines to <c>mcp-audit.log</c> in the app's config
/// directory — separate from the general app log so it's easy to review or ship independently.
/// Deliberately not the same thing as the recorder's "everything on screen" capture (docs/todos.md's
/// Security section) — this logs a redacted, structured summary of each *call*, not raw session
/// output, and content-bearing parameters (SFTP write content, etc.) are never written verbatim (see
/// <see cref="McpBridgeService.RedactParamsForAudit"/>) — the closest thing to §11 rule 3's "no
/// plaintext secrets in logs" this call-metadata log can practically guarantee.
/// </summary>
internal sealed class McpAuditLogger
{
    private readonly string _path;
    private readonly object _lock = new();

    /// <summary><paramref name="path"/> defaults to <c>mcp-audit.log</c> in the app's config directory — overridable so tests don't write to the user's real one.</summary>
    public McpAuditLogger(string? path = null) =>
        _path = path ?? Path.Combine(AppPaths.ConfigDirectory, "mcp-audit.log");

    /// <summary>Appends one JSON-line audit entry. Never throws — a failure to write the audit log must not break the tool call it's recording.</summary>
    public void Log(string tool, string? sessionId, string? profile, string detail, bool success, string? error = null)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            tool,
            session_id = sessionId,
            profile,
            detail,
            success,
            error,
        };

        try
        {
            var line = JsonSerializer.Serialize(entry);
            lock (_lock) { File.AppendAllText(_path, line + Environment.NewLine); }
        }
        catch { /* best-effort — see summary */ }
    }
}
