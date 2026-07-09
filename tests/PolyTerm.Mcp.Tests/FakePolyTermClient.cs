using System.Text.Json;

namespace PolyTerm.Mcp.Tests;

/// <summary>Fake <see cref="IPolyTermClient"/> — lets <see cref="McpServer"/> tests run without a real named pipe or a real PolyTerm.App instance.</summary>
internal sealed class FakePolyTermClient : IPolyTermClient
{
    public List<(string Method, Dictionary<string, JsonElement>? Args)> Calls { get; } = [];

    /// <summary>If set, called to produce the result for every <see cref="CallAsync"/>. Defaults to an empty object.</summary>
    public Func<string, Dictionary<string, JsonElement>?, JsonElement>? OnCall { get; set; }

    /// <summary>If set, thrown from every <see cref="CallAsync"/> instead of returning a result.</summary>
    public Exception? ThrowOnCall { get; set; }

    public Task<JsonElement> CallAsync(string method, object? args = null, CancellationToken ct = default)
    {
        var dict = args as Dictionary<string, JsonElement>;
        Calls.Add((method, dict));

        if (ThrowOnCall is not null) throw ThrowOnCall;

        var result = OnCall?.Invoke(method, dict) ?? JsonDocument.Parse("{}").RootElement.Clone();
        return Task.FromResult(result);
    }
}
