using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Serial;

/// <summary>DI registration for the Serial/COM protocol module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SerialProtocolDescriptor"/> as an <see cref="IProtocolDescriptor"/>, plus
    /// an <see cref="IComPortDetector"/> — WMI-based on Windows, <c>/dev/serial/by-id</c>-based on
    /// Linux (issue #71), a no-op on macOS (issue #91) until it gets its own implementation.
    /// </summary>
    public static IServiceCollection AddPolyTermSerial(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, SerialProtocolDescriptor>();
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IComPortDetector, WmiUsbComPortDetector>();
        else if (OperatingSystem.IsLinux())
            services.AddSingleton<IComPortDetector, LinuxUsbComPortDetector>();
        else
            services.AddSingleton<IComPortDetector, NullComPortDetector>();
        return services;
    }
}
