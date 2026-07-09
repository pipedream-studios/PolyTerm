using PolyTerm.Engine;

namespace PolyTerm.App.McpBridge.Approval;

/// <summary>An MCP action that may require human approval before it's allowed to proceed.</summary>
internal enum McpAction
{
    ConnectionOpen,
    TerminalExecute,
    TerminalSendKeys,
    SftpWrite,
    SftpDelete,
    SftpRename,
    FramebufferClick,
    FramebufferType,
    FramebufferKey,
}

/// <summary>
/// Human-in-the-loop gate in front of state-changing MCP tools (<see cref="McpAction"/>). Sits
/// between <see cref="McpBridgeService"/>'s tool dispatch and the actual engine call — every
/// state-changing tool checks this first and aborts with a clear error if it returns <c>false</c>.
/// Read-only tools (session_list, terminal_read, sftp_list, sftp_read, connection_list) never call
/// this — only actions that change remote or local state do.
/// </summary>
internal interface IApprovalGate
{
    /// <summary>
    /// Returns whether <paramref name="action"/> is allowed to proceed against <paramref name="profile"/>.
    /// <paramref name="detail"/> is a short, human-readable description of exactly what's about to
    /// happen (the command text, the file path, etc.) — shown verbatim in the approval prompt when one
    /// is needed, so it should be safe and meaningful to display as-is.
    /// </summary>
    Task<bool> CheckAsync(McpAction action, ConnectionProfile profile, string detail, CancellationToken ct = default);
}
