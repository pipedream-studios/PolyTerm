using Avalonia;
using Avalonia.Input.TextInput;

namespace PolyTerm.App.Surfaces.Framebuffer;

/// <summary>
/// Tells Avalonia that <see cref="FramebufferSurface"/> takes composed text. Without a client, Avalonia
/// on macOS turns each key into a character by itself, so a dead key such as Option+E types "´" and the
/// E after it a plain "e", never "é". There's no local text to describe: the composed result arrives as
/// ordinary text input, and a half-finished one isn't shown.
/// </summary>
internal sealed class FramebufferTextInputClient(Visual view) : TextInputMethodClient
{
    public override Visual TextViewVisual => view;
    public override bool SupportsPreedit => false;
    public override bool SupportsSurroundingText => false;
    public override string SurroundingText => "";
    public override Rect CursorRectangle => default;
    public override TextSelection Selection { get; set; }
}
