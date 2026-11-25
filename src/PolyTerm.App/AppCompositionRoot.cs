using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PolyTerm.App.McpBridge;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using PolyTerm.Engine.Credentials;
using PolyTerm.Protocols.LocalShell;
using PolyTerm.Protocols.RdpFreerdp;
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

        // Information in Release, Debug in Debug builds; POLYTERM_LOG_LEVEL overrides either (issue #281).
        var logLevelOverride = Environment.GetEnvironmentVariable(AppLogLevel.EnvironmentVariable);
        var logLevel = AppLogLevel.Resolve(logLevelOverride, AppLogLevel.BuildDefault, out var logLevelInvalid);
        // The VNC library's Debug output floods the log and includes keystrokes, so it's filtered (issue #343).
        var vncKeystrokesOptedIn = AppLogFilters.IsOptedIn(Environment.GetEnvironmentVariable(AppLogFilters.VncKeystrokesVariable));
        services.AddLogging(b => AppLogFilters.Apply(b.AddDebug().AddConsole(), logLevel, vncKeystrokesOptedIn));

        // DatabasePath is routed through AppPaths.ConfigDirectory so the POLYTERM_DATA_DIR test-isolation
        // override (see AppPaths.ConfigDirectory's own doc comment) also redirects the connection-profile
        // vault, not just settings/macros.
        // Registered before the engine so TofuHostKeyPolicy picks it up: changed SSH host keys get an
        // interactive trust-once / trust-permanently / disconnect dialog instead of a flat rejection.
        services.AddSingleton<IHostKeyChangePrompt, HostKeyChangePromptService>();
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
        services.AddPolyTermRdpFreerdp();
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

        var provider = services.BuildServiceProvider();
        if (logLevelInvalid)
        {
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AppCompositionRoot))
                .LogWarning("Ignoring {Variable}={Value}: not a log level name (Trace, Debug, Information, Warning, Error, Critical, None). Using {Level}.",
                    AppLogLevel.EnvironmentVariable, logLevelOverride, logLevel);
        }

        if (vncKeystrokesOptedIn)
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AppCompositionRoot));
            if (AppLogFilters.IncludesVncKeystrokes(logLevel, vncKeystrokesOptedIn))
            {
                logger.LogWarning("{Variable} is set: everything typed into a VNC session, including passwords, is written to this log. Don't share it without checking it.",
                    AppLogFilters.VncKeystrokesVariable);
            }
            else
            {
                logger.LogWarning("Ignoring {Variable}: it only works with {LevelVariable}=Trace.",
                    AppLogFilters.VncKeystrokesVariable, AppLogLevel.EnvironmentVariable);
            }
        }

        return provider;
    }
}
