using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PolyTerm.App.McpBridge;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using PolyTerm.Engine.Credentials;
using PolyTerm.Protocols.LocalShell;
using PolyTerm.Protocols.Rdp;
using PolyTerm.Protocols.Serial;
using PolyTerm.Protocols.Ssh;
using PolyTerm.Protocols.Vnc;
using PolyTerm.Protocols.Telnet;
using PolyTerm.Protocols.Web;

namespace PolyTerm.App;

/// <summary>DI composition root for the whole app: registers the engine, protocol modules, MCP bridge, and view models.</summary>
internal static class AppCompositionRoot
{
    /// <summary>Builds the app's <see cref="IServiceProvider"/> — engine + SSH + local-shell protocols, the MCP bridge, and view models.</summary>
    public static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddDebug().AddConsole().SetMinimumLevel(LogLevel.Debug));

        // DatabasePath is routed through AppPaths.ConfigDirectory so the POLYTERM_DATA_DIR test-isolation
        // override (see AppPaths.ConfigDirectory's own doc comment) also redirects the connection-profile
        // vault, not just settings/macros.
        services.AddPolyTermEngine(new EngineOptions
        {
            DatabasePath = Path.Combine(AppPaths.ConfigDirectory, "PolyTerm.db"),
            // Both providers previously had no config path at all in the shipped app (issue #73) —
            // App.Settings.VaultProvider is the on-switch; leaving it "None" (the default) keeps
            // today's behavior exactly (only the OS keychain provider registered).
            KeePass = App.Settings.VaultProvider == "KeePass"
                ? new KeePassProviderOptions { KeePassRpcPort = App.Settings.KeePassRpcPort }
                : null,
            Bitwarden = App.Settings.VaultProvider == "Bitwarden"
                ? new BitwardenProviderOptions { BaseUrl = App.Settings.BitwardenBaseUrl, DeviceIdentifier = App.Settings.BitwardenDeviceId }
                : null,
        });
        services.AddPolyTermSsh();
        services.AddPolyTermLocalShell();
        services.AddPolyTermRdp();
        services.AddPolyTermWeb();
        services.AddPolyTermTelnet();
        services.AddPolyTermSerial();
        services.AddPolyTermVnc();

        // MCP bridge (pipe server for polyterm-mcp.exe). Pipe name defaults to "polyterm" (McpBridgeService's
        // own default) but can be overridden via POLYTERM_MCP_PIPE_NAME - used by PolyTerm.FlaUI.Tests so a
        // test-launched instance never risks colliding with (or a test client connecting to) an already-
        // running real PolyTerm.exe sharing the same well-known pipe name.
        services.AddSingleton<IApprovalGate, ApprovalGateService>();
        services.AddSingleton(sp =>
        {
            var pipeName = Environment.GetEnvironmentVariable("POLYTERM_MCP_PIPE_NAME") ?? "polyterm";
            return ActivatorUtilities.CreateInstance<McpBridgeService>(sp, pipeName);
        });

        // Detected USB COM ports, surfaced as quick-connect entries at the bottom of the connection
        // tree (issue #33). Same explicit Start()/Stop() lifecycle convention as McpBridgeService above.
        services.AddSingleton<ComPortWatcherService>();
        services.AddSingleton<IComPortWatcher>(sp => sp.GetRequiredService<ComPortWatcherService>());

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<ConnectionTreeViewModel>();

        return services.BuildServiceProvider();
    }
}
