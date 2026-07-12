namespace PolyTerm.Engine;

/// <summary>
/// Flags describing what a protocol module can do. The App checks these flags rather than the
/// protocol id, so it never has to special-case a specific protocol (e.g. "does this session
/// support file transfer?" instead of "is this SSH?").
/// </summary>
[Flags]
public enum ProtocolCapabilities
{
    None             = 0,
    /// <summary>Exposes <see cref="IProtocolSession.Terminal"/> — produces a byte stream for a VT emulator.</summary>
    Terminal         = 1 << 0,
    /// <summary>Exposes <see cref="IProtocolSession.FileTransfer"/>.</summary>
    FileTransfer     = 1 << 1,
    /// <summary>
    /// A graphical (non-VT) desktop session — exposes either <see cref="IProtocolSession.Framebuffer"/>
    /// (protocol streams pixels, App renders) or <see cref="IProtocolSession.NativeHost"/> (protocol
    /// renders itself, App embeds its window), depending on <see cref="IProtocolDescriptor.Surface"/>.
    /// </summary>
    GraphicalDesktop = 1 << 2,
    /// <summary>Exposes <see cref="IProtocolSession.Tunnels"/> — local/remote/dynamic port forwarding.</summary>
    PortForwarding   = 1 << 3,
    /// <summary>Supports forwarding X11 client connections over the session (not yet implemented by any protocol).</summary>
    X11Forwarding    = 1 << 4,
    /// <summary>Supports one-shot remote command execution (used by the CLI's exec subcommand).</summary>
    Exec             = 1 << 5,
    /// <summary>Supports transparent auto-reconnect with backoff after an unexpected disconnect.</summary>
    Reconnect        = 1 << 6,
    /// <summary>Safe to target for multi-session input broadcast.</summary>
    InputBroadcast   = 1 << 7,
    /// <summary>Session I/O can be captured to a log/asciicast recording.</summary>
    Recording        = 1 << 8,
    /// <summary>Exposes <see cref="IProtocolSession.Web"/> — an embedded browser surface.</summary>
    WebContent       = 1 << 9,
}
