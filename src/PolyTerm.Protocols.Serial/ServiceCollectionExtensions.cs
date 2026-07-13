using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Serial;

/// <summary>DI registration for the Serial/COM protocol module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SerialProtocolDescriptor"/> as an <see cref="IProtocolDescriptor"/>, plus
    /// an <see cref="IComPortDetector"/> — the real WMI-based one on Windows, a no-op elsewhere.
    /// </summary>
    public static IServiceCollection AddPolyTermSerial(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, SerialProtocolDescriptor>();
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IComPortDetector, WmiUsbComPortDetector>();
        else
            services.AddSingleton<IComPortDetector, NullComPortDetector>();
        return services;
    }
}
