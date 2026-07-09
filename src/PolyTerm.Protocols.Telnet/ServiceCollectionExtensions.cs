using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Telnet;

/// <summary>DI registration for the Telnet/raw/rlogin protocol module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="TelnetProtocolDescriptor"/>, <see cref="RawProtocolDescriptor"/>, and <see cref="RloginProtocolDescriptor"/> as <see cref="IProtocolDescriptor"/>s.</summary>
    public static IServiceCollection AddPolyTermTelnet(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, TelnetProtocolDescriptor>();
        services.AddSingleton<IProtocolDescriptor, RawProtocolDescriptor>();
        services.AddSingleton<IProtocolDescriptor, RloginProtocolDescriptor>();
        return services;
    }
}
