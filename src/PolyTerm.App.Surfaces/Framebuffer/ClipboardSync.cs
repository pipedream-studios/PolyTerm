using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;

namespace PolyTerm.App.Surfaces.Framebuffer;

/// <summary>
/// Text clipboard sync between this computer and one <see cref="IFramebufferChannel"/> (issue #391).
/// One per session, kept as long as its tab, so text copied on the remote arrives even once another tab
/// is showing. When to send the local clipboard is up to <see cref="FramebufferSurface"/>, which only
/// does while it has focus in the active window. Use from the UI thread.
/// </summary>
/// <remarks>
/// Local text is only sent when it has changed since it was last seen here. Above all, what the local
/// clipboard held before text arrived from the remote is never sent back over that text, neither while
/// the new text is still on its way to the local clipboard nor if writing it there fails.
/// </remarks>
public sealed class ClipboardSync : IDisposable
{
    private readonly IFramebufferChannel _channel;
    private readonly ILogger _logger;
    private ILocalClipboard? _local;

    /// <summary>The local clipboard's text when last read or written here.</summary>
    private string? _lastLocal;

    /// <summary>Set when remote text couldn't be written locally: what's there now is older than the remote's.</summary>
    private bool _localIsStale;

    private Task _writes = Task.CompletedTask;
    private int _remoteArrivals; // counted on the channel's thread, as the text arrives
    private int _remoteHandled;
    private bool _reading;
    private bool _disposed;

    public ClipboardSync(IFramebufferChannel channel, ILogger? logger = null)
    {
        _channel = channel;
        _logger = logger ?? NullLogger.Instance;
        channel.ClipboardReceived += OnClipboardReceived;
    }

    /// <summary>Sets the clipboard to sync with. Until this is called, nothing is read or written.</summary>
    public void UseClipboard(ILocalClipboard local) => _local = local;

    /// <summary>Offers the local clipboard's text to the remote, if there's anything new in it.</summary>
    public async Task SendLocalAsync()
    {
        if (_disposed || _reading || _local is not { } local) return;
        var arrivals = Volatile.Read(ref _remoteArrivals);
        if (arrivals != _remoteHandled) return; // remote text is still on its way to the local clipboard

        _reading = true;
        try
        {
            var text = await local.ReadTextAsync();
            // Remote text that arrived during the read makes what was read out of date.
            if (_disposed || arrivals != Volatile.Read(ref _remoteArrivals)) return;

            var stale = _localIsStale;
            _localIsStale = false;
            if (string.IsNullOrEmpty(text) || text == _lastLocal) return;
            _lastLocal = text;
            if (!stale)
                await _channel.SendClipboardAsync(new ClipboardPayload { Text = text });
        }
        catch (Exception ex)
        {
            // Another program can hold the clipboard for a moment. This is retried shortly, so it's only
            // worth a debug line.
            _logger.LogDebug(ex, "Clipboard sync: couldn't send the local clipboard.");
        }
        finally
        {
            _reading = false;
        }
    }

    // Raised on a channel thread that disposing the channel waits for, so this must not block.
    private void OnClipboardReceived(object? sender, ClipboardPayload payload)
    {
        if (!string.Equals(payload.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase)) return;
        Interlocked.Increment(ref _remoteArrivals);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _writes = WriteLocalAsync(_writes, payload.Text));
    }

    /// <summary>Writes one at a time, in the order they arrived. Never throws.</summary>
    private async Task WriteLocalAsync(Task previous, string text)
    {
        await previous;
        try
        {
            if (_disposed || _local is not { } local) return;
            await local.WriteTextAsync(text);
            _lastLocal = text;
            _localIsStale = false;
        }
        catch (Exception ex)
        {
            _localIsStale = true;
            _logger.LogWarning(ex, "Clipboard sync: couldn't put text from the remote on the local clipboard.");
        }
        finally
        {
            _remoteHandled++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.ClipboardReceived -= OnClipboardReceived;
    }
}
