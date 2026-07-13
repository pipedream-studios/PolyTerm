using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Cli.Commands;

/// <summary>
/// Manages saved connection profiles from the CLI: list, quick-create, and delete.
/// </summary>
internal static class ProfilesCommand
{
    /// <summary>Builds the <c>profiles</c> command tree (list/add/delete) wired to the engine's <see cref="IProfileStore"/>.</summary>
    public static Command Build(IServiceProvider services)
    {
        var root = new Command("profiles", "Manage connection profiles");

        // profiles list
        var listCmd = new Command("list", "List all saved profiles");
        listCmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var store    = services.GetRequiredService<IProfileStore>();
            var profiles = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"{"ID",-38} {"Protocol",-12} {"Name",-30} Host");
            Console.WriteLine(new string('-', 90));
            foreach (var p in profiles)
                Console.WriteLine($"{p.Id,-38} {p.ProtocolId,-12} {p.Name,-30} {p.Host}:{p.Port}");
            return 0;
        });

        // profiles add (quick connect to create a persistent profile)
        var nameOpt  = new Option<string>("--name")      { Description = "Profile display name", Required = true };
        var protoOpt = new Option<string>("--protocol")  { Description = "Protocol id (ssh, local-shell)", Required = true };
        var hostOpt  = new Option<string?>("--host")     { Description = "Hostname or IP" };
        var portOpt  = new Option<int?>("--port")        { Description = "Port number" };
        var userOpt  = new Option<string?>("--username") { Description = "Username" };

        var addCmd = new Command("add", "Create a new profile") { nameOpt, protoOpt, hostOpt, portOpt, userOpt };
        addCmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name  = parseResult.GetValue(nameOpt)!;
            var proto = parseResult.GetValue(protoOpt)!;
            var host  = parseResult.GetValue(hostOpt);
            var port  = parseResult.GetValue(portOpt);
            var user  = parseResult.GetValue(userOpt);

            var store = services.GetRequiredService<IProfileStore>();
            var settings = new Dictionary<string, JsonElement>();
            if (user is not null)
                settings["username"] = JsonSerializer.SerializeToElement(user);

            var profile = new ConnectionProfile
            {
                Name       = name,
                ProtocolId = proto,
                Host       = host,
                Port       = port,
                Settings   = settings,
            };
            await store.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Created profile {profile.Id}");
            return 0;
        });

        // profiles delete
        var idArg = new Argument<Guid>("id") { Description = "Profile ID to delete" };
        var delCmd = new Command("delete", "Delete a profile") { idArg };
        delCmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var id = parseResult.GetValue(idArg);
            var store = services.GetRequiredService<IProfileStore>();
            await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Deleted {id}");
            return 0;
        });

        root.Subcommands.Add(listCmd);
        root.Subcommands.Add(addCmd);
        root.Subcommands.Add(delCmd);

        return root;
    }
}
