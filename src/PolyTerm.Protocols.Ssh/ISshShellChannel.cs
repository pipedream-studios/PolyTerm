using PolyTerm.Engine;

namespace PolyTerm.Protocols.Ssh;

/// <summary>
/// An SSH terminal channel that can tell <see cref="SshSession"/> when the remote shell has ended on
/// its own (<c>exit</c>, logout, the shell process dying): the server closed the shell's channel with
/// SSH_MSG_CHANNEL_CLOSE. A dropped TCP connection does not count, since SSH.NET only reports a channel
/// as closed once the server's close message has actually arrived.
/// </summary>
internal interface ISshShellChannel : ITerminalChannel
{
    /// <summary>
    /// True once the server has closed the shell channel. SSH.NET handles the close message on its
    /// message-listener thread before it reports the connection teardown that follows, so this is
    /// already true by the time <see cref="Renci.SshNet.BaseClient.ErrorOccurred"/> fires for that teardown.
    /// </summary>
    bool HasShellEnded { get; }

    /// <summary>Raised once when the server closes the shell channel. May be raised on any thread.</summary>
    event EventHandler? ShellEnded;
}
