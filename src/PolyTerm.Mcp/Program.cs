using PolyTerm.Mcp;

// Silence all stdout-bound logging so the MCP JSON-RPC stream stays clean
Console.OutputEncoding = System.Text.Encoding.UTF8;

var pipeName = Environment.GetEnvironmentVariable("POLYTERM_PIPE") ?? "polyterm";

// No connect here — PolyTermClient connects on the first tool call, so the MCP host can start this
// server before PolyTerm is running (and keep it across PolyTerm restarts).
using var client = new PolyTermClient(pipeName);
await Console.Error.WriteLineAsync($"[polyterm-mcp] Ready; will connect to PolyTerm on pipe '{pipeName}' on first tool call");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var server = new McpServer(client);
await server.RunAsync(Console.In, Console.Out, cts.Token);
return 0;
