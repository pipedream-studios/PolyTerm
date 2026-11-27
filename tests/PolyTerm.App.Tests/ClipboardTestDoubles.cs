using PolyTerm.App.Surfaces.Framebuffer;
using PolyTerm.Engine;

namespace PolyTerm.App.Tests;

/// <summary>A framebuffer channel that records the clipboard text it's sent and can play the remote copying some.</summary>
internal sealed class ClipboardChannel : IFramebufferChannel
{
    private EventHandler<ClipboardPayload>? _clipboardReceived;

    public List<string> Sent { get; } = [];
    public bool SharesClipboard { get; init; } = true;
    public bool HasClipboardListener => _clipboardReceived is not null;

    public int Width => 100;
    public int Height => 100;
    public FramebufferFormat Format => FramebufferFormat.Rgba32;
    public event EventHandler<FramebufferRegionUpdatedEventArgs>? RegionUpdated { add { } remove { } }

    public event EventHandler<ClipboardPayload>? ClipboardReceived
    {
        add => _clipboardReceived += value;
        remove => _clipboardReceived -= value;
    }

    public void CopyOnRemote(string text) => _clipboardReceived?.Invoke(this, new ClipboardPayload { Text = text });

    public ValueTask SendClipboardAsync(ClipboardPayload payload, CancellationToken ct = default)
    {
        Sent.Add(payload.Text);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask SendCtrlAltDeleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;
    public byte[]? CaptureSnapshot() => null;
}

/// <summary>A local clipboard whose reads and writes can be held open or made to fail.</summary>
internal sealed class FakeLocalClipboard : ILocalClipboard
{
    public string? Text { get; set; }
    public int Reads { get; private set; }
    public List<string> Written { get; } = [];
    public bool FailReads { get; set; }
    public bool FailWrites { get; set; }
    public TaskCompletionSource? HoldReads { get; set; }
    public TaskCompletionSource? HoldWrites { get; set; }

    /// <summary>Returns what the clipboard held when the read started.</summary>
    public async Task<string?> ReadTextAsync()
    {
        Reads++;
        var held = Text;
        if (HoldReads is { } hold) await hold.Task;
        if (FailReads) throw new InvalidOperationException("clipboard busy");
        return held;
    }

    public async Task WriteTextAsync(string text)
    {
        if (HoldWrites is { } hold) await hold.Task;
        if (FailWrites) throw new InvalidOperationException("clipboard busy");
        Written.Add(text);
        Text = text;
    }
}
