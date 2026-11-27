using Avalonia.Input.Platform;

namespace PolyTerm.App.Surfaces.Framebuffer;

/// <summary>This computer's clipboard, as much of it as <see cref="ClipboardSync"/> uses.</summary>
public interface ILocalClipboard
{
    /// <summary>The clipboard's text, or null if it holds none.</summary>
    Task<string?> ReadTextAsync();

    Task WriteTextAsync(string text);
}

/// <summary>The clipboard Avalonia gives a window.</summary>
internal sealed class AvaloniaLocalClipboard(IClipboard clipboard) : ILocalClipboard
{
    public Task<string?> ReadTextAsync() => clipboard.TryGetTextAsync();

    public Task WriteTextAsync(string text) => clipboard.SetTextAsync(text);
}
