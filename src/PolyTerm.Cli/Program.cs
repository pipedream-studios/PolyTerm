using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PolyTerm.Cli.Commands;
using PolyTerm.Engine;
using PolyTerm.Protocols.LocalShell;
using PolyTerm.Protocols.Serial;
using PolyTerm.Protocols.Ssh;
using PolyTerm.Protocols.Telnet;
using PolyTerm.Protocols.Web;

namespace PolyTerm.Cli;

internal static class Program
{
    /// <summary>
    /// Entry point: builds the DI host (engine, SSH, local-shell protocols) and dispatches to
    /// the <c>connect</c>/<c>exec</c>/<c>profiles</c> subcommands via System.CommandLine.
    /// </summary>
    internal static async Task<int> Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(s =>
            {
                s.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
                s.AddPolyTermEngine();
                s.AddPolyTermSsh();
                s.AddPolyTermLocalShell();
                s.AddPolyTermWeb();
                s.AddPolyTermTelnet();
                s.AddPolyTermSerial();
            })
            .Build();

        var root = new RootCommand("PolyTerm — headless CLI for SSH and local shells");

        root.Subcommands.Add(ConnectCommand.Build(host.Services));
        root.Subcommands.Add(ExecCommand.Build(host.Services));
        root.Subcommands.Add(ProfilesCommand.Build(host.Services));
        root.Subcommands.Add(HostKeysCommand.Build(host.Services));
        root.Subcommands.Add(AttachCommand.Build());

        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }
}
