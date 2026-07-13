using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Cli.Commands;

/// <summary>
/// One-shot remote execution: connect, send a command line, print output, exit.
/// </summary>
internal static class ExecCommand
{
    public static Command Build(IServiceProvider services)
    {
        var profileIdArg = new Argument<Guid>("profile-id") { Description = "Profile GUID to connect" };
        var commandArg   = new Argument<string>("command")  { Description = "Command to execute" };

        var cmd = new Command("exec", "Execute a single command on a profile and print output") { profileIdArg, commandArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var id      = parseResult.GetValue(profileIdArg);
            var command = parseResult.GetValue(commandArg)!;
            var sm = services.GetRequiredService<ISessionManager>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            IProtocolSession session;
            try
            {
                session = await sm.OpenAsync(id, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Connect failed: {ex.Message}");
                return 1;
            }

            if (session.Terminal is null)
            {
                Console.Error.WriteLine("Session has no terminal channel.");
                return 0;
            }

            var terminal  = session.Terminal;
            var outputBuf = new StringBuilder();
            var doneTcs   = new TaskCompletionSource();

            terminal.OutputReceived += (_, data) =>
            {
                var text = Encoding.UTF8.GetString(data.Span);
                outputBuf.Append(text);
                Console.Write(text);
            };

            session.StateChanged += (_, e) =>
            {
                if (e.Current is SessionState.Closed or SessionState.Faulted)
                    doneTcs.TrySetResult();
            };

            // Send command + newline
            var payload = Encoding.UTF8.GetBytes(command + "\n");
            await terminal.SendInputAsync(payload, cts.Token).ConfigureAwait(false);

            // Wait briefly for output then send exit
            await Task.Delay(500, cts.Token).ConfigureAwait(false);
            await terminal.SendInputAsync("exit\n"u8.ToArray(), cts.Token).ConfigureAwait(false);

            await Task.WhenAny(doneTcs.Task, Task.Delay(5000, cts.Token)).ConfigureAwait(false);
            // CancellationToken.None, not cancellationToken - cleanup must run even if the 60s
            // timeout (or an external Ctrl+C) is what triggered getting here.
            await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            return 0;
        });

        return cmd;
    }
}
