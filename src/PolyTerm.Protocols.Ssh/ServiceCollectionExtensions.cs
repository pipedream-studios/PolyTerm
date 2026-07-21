using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;
using PolyTerm.Protocols.Ssh.X11;

namespace PolyTerm.Protocols.Ssh;

/// <summary>DI registration for the SSH protocol module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SshProtocolDescriptor"/> as an <see cref="IProtocolDescriptor"/>, plus an
    /// <see cref="IBundledX11ServerProvider"/> — the real <see cref="BundledX11Server"/> on Windows
    /// (issue #76), <see cref="NullBundledX11ServerProvider"/> everywhere else. Registered both as the
    /// concrete <see cref="BundledX11Server"/> (so <c>App.axaml.cs</c> can resolve it and drive its
    /// explicit start/stop lifecycle the same way it already does for <c>ComPortWatcherService</c>)
    /// and as the interface (so platform-neutral consumers like <c>SshSession</c> and
    /// <c>NewConnectionViewModel</c> never reference the Windows-only concrete type directly).
    /// </summary>
    public static IServiceCollection AddPolyTermSsh(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, SshProtocolDescriptor>();
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<BundledX11Server>();
            // CA1416 doesn't recognize the OperatingSystem.IsWindows() guard above as protecting
            // this call - the lambda is a separate method for the analyzer's reachability analysis,
            // which can only see a guard in the exact same method as the call it protects (same
            // limitation noted in BundledX11ServerTests/LinuxUsbComPortDetectorTests). This factory
            // only runs when the container resolves IBundledX11ServerProvider, which can't happen
            // before this whole `if` block already ran on the same OS.
#pragma warning disable CA1416
            services.AddSingleton<IBundledX11ServerProvider>(sp => sp.GetRequiredService<BundledX11Server>());
#pragma warning restore CA1416
        }
        else
        {
            services.AddSingleton<IBundledX11ServerProvider>(NullBundledX11ServerProvider.Instance);
        }
        return services;
    }
}
