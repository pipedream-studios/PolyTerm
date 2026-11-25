namespace PolyTerm.Protocols.Vnc;

/// <summary>
/// Controls how the VNC library's send and receive loops are allowed to end (issue #374). Each runs on
/// a raw thread, and the library's own thread wrapper (<c>BackgroundThread</c> in 1.0.0-alpha4) catches
/// every exception <i>except</i> <see cref="OperationCanceledException"/>. One that leaves a loop is
/// therefore unhandled and terminates the whole app, and cancelling the loop is exactly how the library
/// stops it when a connection is closed.
/// </summary>
internal static class VncBackgroundLoop
{
    /// <summary>
    /// Runs <paramref name="loop"/>. Once <paramref name="stopToken"/> is cancelled, however the loop
    /// ends is a normal exit. Before that, an <see cref="OperationCanceledException"/> is a real failure,
    /// so it's wrapped in a type the library does catch and reports as a lost connection.
    /// </summary>
    public static void Run(Action loop, CancellationToken stopToken)
    {
        try
        {
            loop();
        }
        catch (Exception) when (stopToken.IsCancellationRequested)
        {
            // We asked it to stop: the cancellation, or an error from closing the transport under it.
        }
        catch (OperationCanceledException ex)
        {
            throw new IOException("A VNC background operation was cancelled while the connection was still open.", ex);
        }
    }
}
