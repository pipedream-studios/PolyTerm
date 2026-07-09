using PolyTerm.Mcp;

// Silence all stdout-bound logging so the MCP JSON-RPC stream stays clean
Console.OutputEncoding = System.Text.Encoding.UTF8;

var pipeName = Environment.GetEnvironmentVariable("POLYTERM_PIPE") ?? "polyterm";

using var client = new PolyTermClient(pipeName);
try
{
    await client.ConnectAsync(default);
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"[polyterm-mcp] Cannot connect to PolyTerm on pipe '{pipeName}': {ex.Message}");
    await Console.Error.WriteLineAsync("[polyterm-mcp] Make sure PolyTerm is running before starting the MCP server.");
    return 1;
}

await Console.Error.WriteLineAsync($"[polyterm-mcp] Connected to PolyTerm on pipe '{pipeName}'");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var server = new McpServer(client);
await server.RunAsync(Console.In, Console.Out, cts.Token);
return 0;
