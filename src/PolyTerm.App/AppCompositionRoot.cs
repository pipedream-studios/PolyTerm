using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PolyTerm.App.McpBridge;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
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
        });
        services.AddPolyTermSsh();
        services.AddPolyTermLocalShell();
        services.AddPolyTermRdp();
        services.AddPolyTermWeb();
        services.AddPolyTermTelnet();
        services.AddPolyTermSerial();
        services.AddPolyTermVnc();

        // MCP bridge (pipe server for polyterm-mcp.exe)
        services.AddSingleton<IApprovalGate, ApprovalGateService>();
        services.AddSingleton<McpBridgeService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<ConnectionTreeViewModel>();

        return services.BuildServiceProvider();
    }
}
