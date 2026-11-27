using System.Text;
using System.Text.Json;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// One session's text clipboard: converts between <see cref="ClipboardPayload"/> and the remote's
/// <c>CF_UNICODETEXT</c> (UTF-16LE, CRLF line endings, terminating NUL) and decides what's worth passing
/// on in each direction (issue #389). Kept apart from <see cref="RdpFreerdpFramebufferChannel"/> so all
/// of that is testable without the native shim.
/// </summary>
/// <remarks>
/// Text that matches the last text exchanged in either direction is dropped. Sending the remote its own
/// text back would make this client the owner of its clipboard, replacing any formatted version held
/// there with plain text; resending unchanged local text would likewise overwrite whatever was copied on
/// the remote since, such as an image. And Windows announces each copy twice, which would otherwise
/// surface every remote copy twice.
/// </remarks>
internal sealed class RdpClipboard
{
    /// <summary>The most remote clipboard data that's accepted (about 8 million characters). It comes from the server, so it's untrusted.</summary>
    internal const int MaxRemoteBytes = 16 * 1024 * 1024;

    public const string ShareSettingKey = "shareClipboard";

    /// <summary>
    /// Whether a profile shares the clipboard (issue #390). On unless it's explicitly <c>false</c>, so
    /// profiles saved before the option existed share it too.
    /// </summary>
    public static bool IsShared(IReadOnlyDictionary<string, JsonElement> settings) =>
        !(settings.TryGetValue(ShareSettingKey, out var value) && value.ValueKind == JsonValueKind.False);

    private readonly string _localNewLine;
    private readonly object _lock = new();

    /// <summary>The last text sent or received, with LF line endings so the two sides compare equal.</summary>
    private string? _lastExchanged;

    public RdpClipboard() : this(Environment.NewLine) { }

    internal RdpClipboard(string localNewLine) => _localNewLine = localNewLine;

    /// <summary>The bytes to give the remote for new local content, or <see langword="null"/> if there's nothing to send.</summary>
    public byte[]? PrepareToSend(ClipboardPayload payload)
    {
        if (!string.Equals(payload.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
            return null;

        var text = Normalize(payload.Text);
        return Remember(text) ? Encode(text) : null;
    }

    /// <summary>The payload to raise for data from the remote, or <see langword="null"/> if there's nothing new in it.</summary>
    public ClipboardPayload? Receive(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxRemoteBytes)
            return null;

        // A stray odd byte can't be half of anything useful.
        var text = Normalize(Encoding.Unicode.GetString(data[..(data.Length & ~1)]));
        return Remember(text)
            ? new ClipboardPayload { Text = text.Replace("\n", _localNewLine, StringComparison.Ordinal) }
            : null;
    }

    /// <summary>Records <paramref name="text"/> as exchanged. False if it's empty or was the last one already.</summary>
    private bool Remember(string text)
    {
        lock (_lock)
        {
            // Empty means there's nothing to share, not that the other side should be wiped.
            if (text.Length == 0 || text == _lastExchanged)
                return false;
            _lastExchanged = text;
            return true;
        }
    }

    /// <summary>Cuts at the first NUL, which is where <c>CF_UNICODETEXT</c> ends, and turns CRLF into LF.</summary>
    private static string Normalize(string text)
    {
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        if (end >= 0)
            text = text[..end];
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static byte[] Encode(string normalized) =>
        Encoding.Unicode.GetBytes(normalized.Replace("\n", "\r\n", StringComparison.Ordinal) + '\0');
}
