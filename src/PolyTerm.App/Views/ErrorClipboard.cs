using Avalonia.Controls;
using Avalonia.Input.Platform;
using PolyTerm.App.Surfaces.Terminal;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>Copies an error message to the clipboard and says so in the status bar.</summary>
internal static class ErrorClipboard
{
    /// <summary>Empty text, or an <paramref name="owner"/> with no clipboard, leaves the clipboard alone. UI thread only.</summary>
    public static async Task CopyAsync(Control owner, MainViewModel vm, string? text)
    {
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard) return;
        // No ConfigureAwait(false): the notice has to be raised back on the UI thread.
        await clipboard.SetTextAsync(text);
        vm.ShowCopiedNotice(TerminalSurface.CountCharacters(text));
    }
}
