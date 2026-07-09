using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Cli.Commands;

/// <summary>
/// Interactive session: connect to a profile and pipe stdin/stdout to the terminal channel.
/// Architectural proof that the engine is UI-independent.
/// </summary>
internal static class ConnectCommand
{
    public static Command Build(IServiceProvider services)
    {
        var profileIdArg = new Argument<Guid>("profile-id", "Profile GUID to connect");

        var cmd = new Command("connect", "Open an interactive session for a saved profile") { profileIdArg };
        cmd.SetHandler(async (InvocationContext context) =>
        {
            var id = context.ParseResult.GetValueForArgument(profileIdArg);
            var sm = services.GetRequiredService<ISessionManager>();
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            Console.Error.WriteLine($"Connecting to profile {id}…");
            IProtocolSession session;
            try
            {
                session = await sm.OpenAsync(id, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Connect failed: {ex.Message}");
                context.ExitCode = 1;
                return;
            }

            if (session.Terminal is null)
            {
                Console.Error.WriteLine("Session has no terminal channel.");
                return;
            }

            var terminal = session.Terminal;
            var outputDone = new TaskCompletionSource();

            terminal.OutputReceived += (_, data) =>
            {
                Console.OpenStandardOutput().Write(data.Span);
            };

            session.StateChanged += (_, e) =>
            {
                if (e.Current is SessionState.Closed or SessionState.Faulted)
                    outputDone.TrySetResult();
            };

            // Stdin → terminal
            var stdinTask = Task.Run(async () =>
            {
                var buf = new byte[256];
                while (!cts.IsCancellationRequested)
                {
                    var read = await Console.OpenStandardInput().ReadAsync(buf, cts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await terminal.SendInputAsync(new ReadOnlyMemory<byte>(buf, 0, read), cts.Token).ConfigureAwait(false);
                }
            }, cts.Token);

            await Task.WhenAny(outputDone.Task, stdinTask).ConfigureAwait(false);
            await session.DisconnectAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        });

        return cmd;
    }
}
