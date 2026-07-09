using System.Text.Json;

namespace PolyTerm.Mcp;

/// <summary>
/// The RPC surface <see cref="McpServer"/> needs from whatever talks to PolyTerm.App — extracted from
/// the concrete <see cref="PolyTermClient"/> (a real named-pipe client) so <c>McpServer</c>'s own
/// dispatch/schema logic can be tested against a fake implementation, without a real pipe.
/// </summary>
internal interface IPolyTermClient
{
    /// <summary>
    /// Sends a JSON-RPC request and returns the <c>result</c> element.
    /// Throws if the bridge returns an error.
    /// </summary>
    Task<JsonElement> CallAsync(string method, object? args = null, CancellationToken ct = default);
}
