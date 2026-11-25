using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation.Services.Communication;
using MarcusW.VncClient.Protocol.Services;

namespace PolyTerm.Protocols.Vnc;

/// <summary>
/// The library's message sender, made safe to stop (issue #374). Waiting on its send queue throws
/// <see cref="OperationCanceledException"/> when the loop is cancelled, which unguarded terminates the
/// app on close. See <see cref="VncBackgroundLoop"/>. The base class still does all the sending, and
/// logs under its own category, so the app's keystroke log filter (issue #343) still applies.
/// </summary>
internal sealed class VncMessageSender(RfbConnectionContext context) : RfbMessageSender(context), IRfbMessageSender
{
    /// <summary>
    /// Re-implements the interface method, which the base class doesn't make virtual. The library
    /// completes the base task from the send thread and runs whatever awaits it right there, including
    /// disposing this sender, which waits for the send thread to end. So the thread waited for itself,
    /// adding a second to every close. Yielding moves the rest of the close to the thread pool.
    /// </summary>
    public new async Task StopSendLoopAsync()
    {
        await base.StopSendLoopAsync().ConfigureAwait(false);
        await Task.Yield();
    }

    protected override void ThreadWorker(CancellationToken cancellationToken) =>
        VncBackgroundLoop.Run(() => base.ThreadWorker(cancellationToken), cancellationToken);
}
