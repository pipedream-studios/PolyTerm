namespace PolyTerm.Engine;

/// <summary>
/// Asks the user what to do when a host presents a key that differs from the one on record. Implemented by
/// interactive front ends (the GUI); headless hosts (CLI, MCP) register nothing, so a changed key is rejected.
/// </summary>
public interface IHostKeyChangePrompt
{
    /// <summary>Shows <paramref name="info"/> to the user and returns their decision. Must fail closed (<see cref="HostKeyChangeChoice.Disconnect"/>) if no UI is available.</summary>
    Task<HostKeyChangeChoice> AskAsync(HostKeyChangeInfo info, CancellationToken ct = default);
}

/// <summary>The recorded key and the newly presented key for a host whose key has changed.</summary>
public sealed record HostKeyChangeInfo(
    string Host,
    int Port,
    string Algorithm,
    string KnownFingerprintSha256,
    DateTimeOffset KnownFirstSeenAt,
    string NewFingerprintSha256);

/// <summary>The user's answer to an <see cref="IHostKeyChangePrompt"/>.</summary>
public enum HostKeyChangeChoice
{
    /// <summary>Abort the connection and keep the recorded key.</summary>
    Disconnect,
    /// <summary>Connect this time only; the recorded key is left unchanged.</summary>
    TrustOnce,
    /// <summary>Connect and replace the recorded key with the new one.</summary>
    TrustPermanently,
}
