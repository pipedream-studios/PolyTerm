using System.CommandLine;
using System.Text;
using System.Text.Json;

namespace PolyTerm.Cli.Commands;

/// <summary>
/// Attaches to a session already open in a <em>running PolyTerm.App instance</em> — including one it
/// detached but kept alive (issue #77/#93 "On Close = Detach") — over the same named pipe
/// <c>McpBridgeService</c> exposes to PolyTerm.Mcp (issue #114/#116). Unlike <see cref="ConnectCommand"/>/
/// <see cref="ExecCommand"/>, this does not open its own in-process session at all: PolyTerm.App owns
/// the real connection, and this command is a thin remote terminal into it, so it needs no
/// <see cref="IServiceProvider"/> — nothing here talks to <c>ISessionManager</c> directly.
/// </summary>
internal static class AttachCommand
{
    public static Command Build()
    {
        var sessionIdArg = new Argument<string?>("session-id")
        {
            Description = "Session ID to attach to (see --list)",
            Arity       = ArgumentArity.ZeroOrOne,
        };
        var listOpt = new Option<bool>("--list")
        {
            Description = "List sessions available to attach to in the running PolyTerm.App, instead of attaching",
        };

        var cmd = new Command("attach", "Attach to a session already open in a running PolyTerm.App instance") { sessionIdArg, listOpt };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var sessionId = parseResult.GetValue(sessionIdArg);
            var list      = parseResult.GetValue(listOpt);
            var pipeName  = Environment.GetEnvironmentVariable("POLYTERM_PIPE") ?? "polyterm";

            using var client = new AttachPipeClient(pipeName);
            try
            {
                await client.ConnectAsync(5_000, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Cannot connect to PolyTerm on pipe '{pipeName}': {ex.Message}");
                Console.Error.WriteLine("Make sure PolyTerm.App is running.");
                return 1;
            }

            if (list || sessionId is null)
                return await ListSessionsAsync(client, cancellationToken).ConfigureAwait(false);

            return await AttachToSessionAsync(client, sessionId, cancellationToken).ConfigureAwait(false);
        });

        return cmd;
    }

    private static async Task<int> ListSessionsAsync(AttachPipeClient client, CancellationToken ct)
    {
        var result   = await client.CallAsync("session_list", null, ct).ConfigureAwait(false);
        var sessions = result.GetProperty("sessions");

        if (sessions.GetArrayLength() == 0)
        {
            Console.WriteLine("No sessions open in PolyTerm.App.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"Protocol",-12} {"State",-14} Name");
        Console.WriteLine(new string('-', 90));
        foreach (var s in sessions.EnumerateArray())
            Console.WriteLine($"{s.GetProperty("id").GetString(),-38} {s.GetProperty("protocol").GetString(),-12} {s.GetProperty("state").GetString(),-14} {s.GetProperty("name").GetString()}");
        return 0;
    }

    private static async Task<int> AttachToSessionAsync(AttachPipeClient client, string sessionId, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        JsonElement started;
        try
        {
            started = await client.CallAsync("attach_start", new { session_id = sessionId }, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot attach: {ex.Message}");
            return 1;
        }

        Console.Error.WriteLine($"[polyterm attach] Attached to {sessionId} — Ctrl+C to detach.");
        var backlog = started.GetProperty("text").GetString();
        if (!string.IsNullOrEmpty(backlog))
            Console.WriteLine(backlog);
        var since = started.GetProperty("total_lines").GetInt64();

        try
        {
            // Stdin → attach_send_input, forwarded by a background loop while the poll loop below
            // tails new output — same split responsibility as ConnectCommand's stdin task, just over
            // the pipe instead of directly against a local ITerminalChannel. Deliberately not awaited
            // after the poll loop below returns: like ConnectCommand's stdinTask, a blocking console
            // read may never observe cancellation, so exiting the process without joining it is the
            // same accepted tradeoff made there.
            _ = Task.Run(async () =>
            {
                var buf = new byte[256];
                while (!cts.IsCancellationRequested)
                {
                    var read = await Console.OpenStandardInput().ReadAsync(buf, cts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    var text = Encoding.UTF8.GetString(buf, 0, read);
                    await client.CallAsync("attach_send_input", new { session_id = sessionId, data = text }, cts.Token).ConfigureAwait(false);
                }
            }, cts.Token);

            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(200, cts.Token).ConfigureAwait(false);
                var chunk = await client.CallAsync("attach_read", new { session_id = sessionId, since }, cts.Token).ConfigureAwait(false);
                var text  = chunk.GetProperty("text").GetString();
                if (!string.IsNullOrEmpty(text))
                    Console.WriteLine(text);
                since = chunk.GetProperty("total_lines").GetInt64();
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C — fall through to attach_stop below.
        }
        finally
        {
            try { await client.CallAsync("attach_stop", new { session_id = sessionId }, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort release — the bridge also clears this on SessionClosed */ }
        }

        return 0;
    }
}
