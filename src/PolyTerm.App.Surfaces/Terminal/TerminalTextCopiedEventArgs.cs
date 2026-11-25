using Avalonia.Interactivity;

namespace PolyTerm.App.Surfaces.Terminal;

/// <summary>Data for <see cref="TerminalSurface.TextCopiedEvent"/>. Carries the length only, never the copied text.</summary>
public sealed class TerminalTextCopiedEventArgs(int characterCount) : RoutedEventArgs
{
    /// <summary>How many characters went to the clipboard (see <see cref="TerminalSurface.CountCharacters"/>).</summary>
    public int CharacterCount { get; } = characterCount;
}
