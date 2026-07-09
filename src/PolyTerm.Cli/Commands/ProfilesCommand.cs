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
        listCmd.SetHandler(async () =>
        {
            var store    = services.GetRequiredService<IProfileStore>();
            var profiles = await store.GetAllAsync().ConfigureAwait(false);
            Console.WriteLine($"{"ID",-38} {"Protocol",-12} {"Name",-30} Host");
            Console.WriteLine(new string('-', 90));
            foreach (var p in profiles)
                Console.WriteLine($"{p.Id,-38} {p.ProtocolId,-12} {p.Name,-30} {p.Host}:{p.Port}");
        });

        // profiles add (quick connect to create a persistent profile)
        var nameOpt  = new Option<string>("--name",     "Profile display name") { IsRequired = true };
        var protoOpt = new Option<string>("--protocol", "Protocol id (ssh, local-shell)") { IsRequired = true };
        var hostOpt  = new Option<string?>("--host",    "Hostname or IP");
        var portOpt  = new Option<int?>("--port",       "Port number");
        var userOpt  = new Option<string?>("--username","Username");

        var addCmd = new Command("add", "Create a new profile") { nameOpt, protoOpt, hostOpt, portOpt, userOpt };
        addCmd.SetHandler(async (string name, string proto, string? host, int? port, string? user) =>
        {
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
            await store.SaveAsync(profile).ConfigureAwait(false);
            Console.WriteLine($"Created profile {profile.Id}");
        }, nameOpt, protoOpt, hostOpt, portOpt, userOpt);

        // profiles delete
        var idArg = new Argument<Guid>("id", "Profile ID to delete");
        var delCmd = new Command("delete", "Delete a profile") { idArg };
        delCmd.SetHandler(async (Guid id) =>
        {
            var store = services.GetRequiredService<IProfileStore>();
            await store.DeleteAsync(id).ConfigureAwait(false);
            Console.WriteLine($"Deleted {id}");
        }, idArg);

        root.AddCommand(listCmd);
        root.AddCommand(addCmd);
        root.AddCommand(delCmd);

        return root;
    }
}
