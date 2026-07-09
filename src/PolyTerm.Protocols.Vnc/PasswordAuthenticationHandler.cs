using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.SecurityTypes;
using MarcusW.VncClient.Security;

namespace PolyTerm.Protocols.Vnc;

/// <summary>
/// Supplies the classic RFB "VNC Authentication" password when the library asks for it. Standard VNC
/// auth is password-only (no username concept, unlike SSH/RDP) — any other requested input type isn't
/// supported by this first rung.
/// </summary>
internal sealed class PasswordAuthenticationHandler : IAuthenticationHandler
{
    private readonly string _password;

    public PasswordAuthenticationHandler(string password) => _password = password;

    public Task<TInput> ProvideAuthenticationInputAsync<TInput>(RfbConnection connection, ISecurityType securityType,
        IAuthenticationInputRequest<TInput> request) where TInput : class, IAuthenticationInput
    {
        if (typeof(TInput) == typeof(PasswordAuthenticationInput))
            return Task.FromResult((TInput)(object)new PasswordAuthenticationInput(_password));

        throw new NotSupportedException($"Authentication input type '{typeof(TInput).Name}' is not supported.");
    }
}
