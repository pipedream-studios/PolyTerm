namespace PolyTerm.Engine;

/// <summary>Raised via <see cref="IProtocolSession.StateChanged"/> whenever a session's lifecycle state changes.</summary>
public sealed class SessionStateChangedEventArgs : EventArgs
{
    public required SessionState Previous { get; init; }
    public required SessionState Current { get; init; }
}

/// <summary>Raised via <see cref="IProtocolSession.ErrorOccurred"/> when a session encounters an error.</summary>
public sealed class SessionErrorEventArgs : EventArgs
{
    public required Exception Exception { get; init; }

    /// <summary>True if the error ended the session (as opposed to a recoverable warning).</summary>
    public required bool IsFatal { get; init; }
}

/// <summary>Raised via <see cref="ISessionManager.SessionOpened"/>/<see cref="ISessionManager.SessionClosed"/>.</summary>
public sealed class SessionEventArgs : EventArgs
{
    public required IProtocolSession Session { get; init; }
}

/// <summary>Raised via <see cref="IFramebufferChannel.RegionUpdated"/> when a rectangular region of the remote desktop changes.</summary>
public sealed class FramebufferRegionUpdatedEventArgs : EventArgs
{
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Raw pixel data in the channel's declared format.</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>Clipboard content exchanged with a graphical-desktop session.</summary>
public sealed class ClipboardPayload
{
    public required string Text { get; init; }
    public string MimeType { get; init; } = "text/plain";
}

/// <summary>Progress reported during a file upload/download.</summary>
public sealed class TransferProgress
{
    public required long BytesTransferred { get; init; }
    /// <summary>Null when the total size isn't known in advance.</summary>
    public long? TotalBytes { get; init; }
    public string? CurrentPath { get; init; }
}
