using System.Text.Json;

namespace PolyTerm.Mcp;

/// <summary>
/// Minimal MCP JSON-RPC 2.0 server over stdio.
/// Implements the 2024-11-05 protocol version: initialize, tools/list, tools/call, ping.
/// Tool calls are proxied to PolyTerm.App via <see cref="PolyTermClient"/>.
/// </summary>
internal sealed class McpServer(IPolyTermClient client)
{
    /// <summary>
    /// Reads newline-delimited JSON-RPC requests from <paramref name="stdin"/> and writes responses
    /// to <paramref name="stdout"/> until the stream ends or <paramref name="ct"/> is cancelled.
    /// Malformed requests get a JSON-RPC parse-error response rather than tearing down the loop;
    /// notifications (messages with no <c>id</c>) are processed but produce no response.
    /// </summary>
    public async Task RunAsync(TextReader stdin, TextWriter stdout, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await stdin.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? response;
            try   { response = await HandleMessageAsync(line, ct).ConfigureAwait(false); }
            catch (Exception ex) { response = TryBuildParseError(line, ex.Message); }

            if (response is null) continue; // notification — no response

            await stdout.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
            await stdout.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<string?> HandleMessageAsync(string line, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        // Notifications (no id) — acknowledge but don't respond
        if (!root.TryGetProperty("id", out var idProp)) return null;

        var id     = (object)(idProp.ValueKind == JsonValueKind.Null ? null! : idProp.GetInt32());
        var method = root.GetProperty("method").GetString()!;
        root.TryGetProperty("params", out var @params);

        try
        {
            var result = await DispatchAsync(method, @params, ct).ConfigureAwait(false);
            return Serialize(new { jsonrpc = "2.0", id, result });
        }
        catch (Exception ex)
        {
            return Serialize(new { jsonrpc = "2.0", id, error = new { code = -32000, message = ex.Message } });
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement @params, CancellationToken ct) =>
        method switch
        {
            "initialize"  => Initialize(),
            "ping"        => new { },
            "tools/list"  => ToolsList(),
            "tools/call"  => await ToolsCallAsync(@params, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Method not found: {method}"),
        };

    // ── MCP protocol responses ────────────────────────────────────────────────

    private static object Initialize() => new
    {
        protocolVersion = "2024-11-05",
        capabilities    = new { tools = new { } },
        serverInfo      = new { name = "PolyTerm", version = "1.0.0" },
    };

    private static object ToolsList() => new
    {
        tools = new[]
        {
            Tool("session_list",
                 "List all open terminal sessions in PolyTerm.",
                 Schema()),

            Tool("connection_list",
                 "List all saved connections in PolyTerm's vault (not just currently-open sessions). " +
                 "Use this to discover what's available before calling connection_open.",
                 Schema()),

            Tool("connection_open",
                 "Open a new session from a saved connection, by name or by exact profile_id " +
                 "(at least one required; profile_id wins if both are given). Name matching is " +
                 "case-insensitive: an exact match wins outright, otherwise a substring match is only " +
                 "accepted if it's unambiguous. May require human approval depending on the " +
                 "connection's MCP policy — if denied, the error explains why. On success, the new " +
                 "session's ID is usable with terminal_*/sftp_* tools just like any session from " +
                 "session_list, and it appears as a real tab in the running app.",
                 Schema(
                     Prop("name",       "string", "Connection name to search for (fuzzy)"),
                     Prop("profile_id", "string", "Exact saved-connection ID (takes precedence over name)")),
                 required: []),

            Tool("connection_close",
                 "Close an open session by ID.",
                 Schema(
                     Prop("session_id", "string", "Session ID from session_list or connection_open")),
                 required: ["session_id"]),

            Tool("terminal_read",
                 "Read the last N lines from a session's terminal output buffer.",
                 Schema(
                     Prop("session_id", "string", "Session ID from session_list"),
                     Prop("lines",      "integer","Lines to return (default 50)")),
                 required: ["session_id"]),

            Tool("terminal_execute",
                 "Send a command to a terminal session and return its output. " +
                 "Waits up to timeout_seconds for the output to settle. " +
                 "May require human approval depending on the session's MCP policy.",
                 Schema(
                     Prop("session_id",      "string",  "Session ID from session_list"),
                     Prop("command",         "string",  "Shell command to run"),
                     Prop("timeout_seconds", "integer", "Max wait time in seconds (default 30)")),
                 required: ["session_id", "command"]),

            Tool("terminal_send_keys",
                 "Send raw key input to a terminal session. " +
                 "Use \\u0003 for Ctrl+C, \\u0004 for Ctrl+D, etc. " +
                 "May require human approval depending on the session's MCP policy.",
                 Schema(
                     Prop("session_id", "string", "Session ID from session_list"),
                     Prop("keys",       "string", "String sent verbatim to the terminal")),
                 required: ["session_id", "keys"]),

            Tool("sftp_list",
                 "List files and directories at a remote path via SFTP.",
                 Schema(
                     Prop("session_id", "string", "Session ID — must have has_sftp=true"),
                     Prop("path",       "string", "Remote directory path (default /)")),
                 required: ["session_id"]),

            Tool("sftp_read",
                 "Download and return the text content of a remote file (max 200 KB). " +
                 "Only works for UTF-8 text files.",
                 Schema(
                     Prop("session_id", "string", "Session ID — must have has_sftp=true"),
                     Prop("path",       "string", "Absolute remote file path")),
                 required: ["session_id", "path"]),

            Tool("sftp_write",
                 "Write (create or overwrite) a remote file with the given text content. " +
                 "May require human approval depending on the session's MCP policy.",
                 Schema(
                     Prop("session_id", "string", "Session ID — must have has_sftp=true"),
                     Prop("path",       "string", "Absolute remote file path"),
                     Prop("content",    "string", "UTF-8 text to write")),
                 required: ["session_id", "path", "content"]),

            Tool("sftp_delete",
                 "Delete a remote file or empty directory. " +
                 "May require human approval depending on the session's MCP policy.",
                 Schema(
                     Prop("session_id", "string", "Session ID — must have has_sftp=true"),
                     Prop("path",       "string", "Absolute remote path to delete")),
                 required: ["session_id", "path"]),

            Tool("sftp_rename",
                 "Rename or move a remote file or directory. " +
                 "May require human approval depending on the session's MCP policy.",
                 Schema(
                     Prop("session_id",   "string", "Session ID — must have has_sftp=true"),
                     Prop("source",       "string", "Current absolute remote path"),
                     Prop("destination",  "string", "New absolute remote path")),
                 required: ["session_id", "source", "destination"]),

            Tool("framebuffer_screenshot",
                 "Capture a screenshot of an RDP or VNC session's desktop as a PNG image.",
                 Schema(
                     Prop("session_id", "string", "Session ID from session_list — must be RDP or VNC")),
                 required: ["session_id"]),

            Tool("framebuffer_click",
                 "Click at a coordinate on an RDP or VNC session's desktop (coordinates are in the " +
                 "session's own screen space, from framebuffer_screenshot). " +
                 "May require human approval depending on the session's MCP policy.",
                 Schema(
                     Prop("session_id", "string", "Session ID from session_list — must be RDP or VNC"),
                     Prop("x",          "integer","X coordinate"),
                     Prop("y",          "integer","Y coordinate"),
                     Prop("button",     "string", "left, right, or middle (default left)")),
                 required: ["session_id", "x", "y"]),

            Tool("framebuffer_type",
                 "Type literal text into an RDP or VNC session's desktop (types into whatever " +
                 "currently has focus there — click a field first if needed). " +
                 "May require human approval depending on the session's MCP policy.",
                 Schema(
                     Prop("session_id", "string", "Session ID from session_list — must be RDP or VNC"),
                     Prop("text",       "string", "Text to type")),
                 required: ["session_id", "text"]),

            Tool("framebuffer_key",
                 "Press a single named key on an RDP or VNC session's desktop — e.g. Return, Tab, " +
                 "Escape, BackSpace, Delete, Up/Down/Left/Right, Home, End, Prior/Next (page up/down), " +
                 "F1-F12. Use framebuffer_type for literal text instead. " +
                 "May require human approval depending on the session's MCP policy.",
                 Schema(
                     Prop("session_id", "string", "Session ID from session_list — must be RDP or VNC"),
                     Prop("key",        "string", "Named key, e.g. \"Return\", \"Escape\", \"F5\"")),
                 required: ["session_id", "key"]),

            Tool("framebuffer_resize",
                 "Resize an RDP or VNC session's remote desktop resolution (best-effort — not every " +
                 "server honors it).",
                 Schema(
                     Prop("session_id", "string", "Session ID from session_list — must be RDP or VNC"),
                     Prop("width",      "integer","New width in pixels"),
                     Prop("height",     "integer","New height in pixels")),
                 required: ["session_id", "width", "height"]),
        },
    };

    // ── Tool call dispatch ────────────────────────────────────────────────────

    private async Task<object> ToolsCallAsync(JsonElement @params, CancellationToken ct)
    {
        var name    = @params.GetProperty("name").GetString()!;
        var hasArgs = @params.TryGetProperty("arguments", out var argsEl);
        var args    = hasArgs && argsEl.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsEl)
            : null;

        var result = await client.CallAsync(name, args, ct).ConfigureAwait(false);

        // framebuffer_screenshot returns an actual image content block (so the agent can see the
        // screenshot, not just a text description of it) rather than the generic text/raw-JSON
        // fallback every other tool uses.
        if (result.TryGetProperty("image_base64", out var img))
        {
            return new
            {
                content = new object[]
                {
                    new { type = "image", data = img.GetString() ?? "", mimeType = "image/png" },
                },
            };
        }

        // Extract the human-readable text; fall back to raw JSON for structured results
        var text = result.TryGetProperty("text", out var t) ? t.GetString() ?? "" : result.GetRawText();

        return new { content = new[] { new { type = "text", text } } };
    }

    // ── Schema helpers ────────────────────────────────────────────────────────

    private static object Tool(string name, string description, object inputSchema, string[]? required = null)
    {
        _ = required; // schema already captures required via Schema() overloads
        return new { name, description, inputSchema };
    }

    private static object Schema(params object[] props)
    {
        if (props.Length == 0)
            return new { type = "object", properties = new { } };

        // props are (name, type, desc) triples built by Prop()
        var dict = new Dictionary<string, object>();
        foreach (var p in props)
        {
            var tuple = (ValueTuple<string, object>)p;
            dict[tuple.Item1] = tuple.Item2;
        }
        return new { type = "object", properties = dict };
    }

    private static (string, object) Prop(string name, string type, string description) =>
        (name, new { type, description });

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string Serialize(object o) => JsonSerializer.Serialize(o);

    private static string? TryBuildParseError(string line, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("id", out var id)) return null;
            var idVal = (object)(id.ValueKind == JsonValueKind.Null ? null! : id.GetInt32());
            return Serialize(new { jsonrpc = "2.0", id = idVal, error = new { code = -32700, message } });
        }
        catch { return null; }
    }
}
