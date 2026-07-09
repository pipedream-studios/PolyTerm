namespace PolyTerm.Engine;

/// <summary>
/// Lifecycle states of an <see cref="IProtocolSession"/>. Normal path: Created → Connecting →
/// Connected → Closing → Closed. An unexpected disconnect goes to Faulted, unless the protocol
/// supports <see cref="ProtocolCapabilities.Reconnect"/>, in which case it goes to Reconnecting
/// instead and returns to Connected on success (or keeps retrying — see the SSH module).
/// </summary>
public enum SessionState
{
    Created,
    Connecting,
    Connected,
    Closing,
    Closed,
    /// <summary>Connection failed or dropped and no reconnect is in progress (or reconnect isn't supported).</summary>
    Faulted,
    /// <summary>Was connected, dropped unexpectedly, and is retrying with backoff. Not a terminal state — it returns to <see cref="Connected"/> on success.</summary>
    Reconnecting,
}
