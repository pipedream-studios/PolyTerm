using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine.Credentials;
using PolyTerm.Engine.HostKeyPolicy;
using PolyTerm.Engine.Storage;

namespace PolyTerm.Engine;

public static class EngineServiceCollectionExtensions
{
    /// <summary>
    /// Registers all core engine services. Call protocol-specific AddPolyTermXxx() extensions after this.
    /// </summary>
    public static IServiceCollection AddPolyTermEngine(
        this IServiceCollection services,
        EngineOptions? options = null)
    {
        options ??= new EngineOptions();
        var dbPath = options.DatabasePath ?? DefaultDbPath();

        services.AddSingleton<IProfileStore>(sp =>
            new ProfileStore(dbPath, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProfileStore>>()));

        services.AddSingleton<IHostKeyStore>(sp =>
            new HostKeyStore(dbPath, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HostKeyStore>>()));

        services.AddSingleton<IHostKeyPolicy, TofuHostKeyPolicy>();
        services.AddSingleton<IProtocolRegistry, ProtocolRegistry>();
        services.AddSingleton<ISessionManager, SessionManager>();

        // OS keychain — single instance registered under both interfaces
        services.AddSingleton<OsKeychainProvider>();
        services.AddSingleton<ICredentialProvider>(sp => sp.GetRequiredService<OsKeychainProvider>());
        services.AddSingleton<ICredentialWriter>(sp => sp.GetRequiredService<OsKeychainProvider>());

        if (options.KeePass is not null)
        {
            services.AddSingleton(options.KeePass);
            services.AddSingleton<ICredentialProvider, KeePassProvider>();
        }

        if (options.Bitwarden is not null)
        {
            services.AddSingleton(options.Bitwarden);
            services.AddSingleton<ICredentialProvider, BitwardenProvider>();
            services.AddSingleton<BitwardenCliController>();
        }

        return services;
    }

    private static string DefaultDbPath()
    {
        // POLYTERM_DATA_DIR, when set, overrides this entirely - the same override PolyTerm.App's
        // AppPaths.ConfigDirectory implements (see its own doc comment) so PolyTerm.FlaUI.Tests can
        // point a launched PolyTerm.exe at an isolated, disposable directory instead of the real
        // per-user vault. PolyTerm.App always passes an explicit DatabasePath through AppPaths
        // itself and never reaches this fallback, so honoring the same env var here is what actually
        // gives PolyTerm.Cli (which calls AddPolyTermEngine() with no options) the same isolation -
        // without it, every CLI invocation always wrote straight into the real production vault with
        // no way to opt out, confirmed the hard way when a manual smoke test of the CLI did exactly
        // that.
        var overrideDir = Environment.GetEnvironmentVariable("POLYTERM_DATA_DIR");
        var dir = !string.IsNullOrEmpty(overrideDir)
            ? overrideDir
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PolyTerm")
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   "Library", "Application Support", "PolyTerm")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   ".config", "PolyTerm");

        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "PolyTerm.db");
    }
}

/// <summary>
/// Configuration for <see cref="EngineServiceCollectionExtensions.AddPolyTermEngine"/>.
/// Leaving <see cref="KeePass"/> or <see cref="Bitwarden"/> null skips registering
/// that credential provider entirely — the OS keychain provider is always registered.
/// </summary>
public sealed class EngineOptions
{
    /// <summary>Path to the SQLite database file. Defaults to a per-OS app-data location if unset.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Enables the KeePass (KeePassRPC) credential provider when set.</summary>
    public KeePassProviderOptions?   KeePass   { get; set; }

    /// <summary>Enables the Bitwarden (<c>bw serve</c>) credential provider when set.</summary>
    public BitwardenProviderOptions? Bitwarden { get; set; }
}
