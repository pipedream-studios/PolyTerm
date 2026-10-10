using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Cli.Commands;

/// <summary>
/// Manages trusted SSH host keys from the CLI: list and forget (e.g. after a legitimate key rotation).
/// </summary>
internal static class HostKeysCommand
{
    /// <summary>Builds the <c>hostkeys</c> command tree (list/forget) wired to the engine's <see cref="IHostKeyStore"/>.</summary>
    public static Command Build(IServiceProvider services)
    {
        var root = new Command("hostkeys", "Manage trusted SSH host keys");

        // hostkeys list
        var listCmd = new Command("list", "List all trusted host keys");
        listCmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IHostKeyStore>();
            var keys  = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"{"Host",-40} {"Port",-6} {"Algorithm",-18} Fingerprint");
            Console.WriteLine(new string('-', 110));
            foreach (var k in keys)
                Console.WriteLine($"{k.Host,-40} {k.Port,-6} {k.Algorithm,-18} {k.FingerprintSha256}");
            return 0;
        });

        // hostkeys forget
        var hostArg = new Argument<string>("host") { Description = "Host name or IP" };
        var portOpt = new Option<int>("--port") { Description = "Port number", DefaultValueFactory = _ => 22 };
        var forgetCmd = new Command("forget", "Forget the trusted keys for a host (all algorithms)") { hostArg, portOpt };
        forgetCmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var host  = parseResult.GetValue(hostArg)!;
            var port  = parseResult.GetValue(portOpt);
            var store = services.GetRequiredService<IHostKeyStore>();
            await store.RemoveAsync(host, port, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Forgot host keys for {host}:{port}");
            return 0;
        });

        root.Subcommands.Add(listCmd);
        root.Subcommands.Add(forgetCmd);
        return root;
    }
}
