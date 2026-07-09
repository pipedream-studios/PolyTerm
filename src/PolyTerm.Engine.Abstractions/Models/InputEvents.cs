namespace PolyTerm.Engine;

/// <summary>A mouse event sent to a graphical-desktop session via <see cref="IFramebufferChannel.SendPointerAsync"/>.</summary>
public sealed class PointerEvent
{
    public required int X { get; init; }
    public required int Y { get; init; }
    public required PointerButton Button { get; init; }
    public required PointerEventKind Kind { get; init; }
}

public enum PointerButton { None, Left, Middle, Right, WheelUp, WheelDown }
public enum PointerEventKind { Move, Press, Release }

/// <summary>A keyboard event sent to a graphical-desktop session via <see cref="IFramebufferChannel.SendKeyAsync"/>.</summary>
public sealed class KeyEvent
{
    /// <summary>Key identifier (not a raw scan code) — the exact symbol set is protocol-defined.</summary>
    public required string KeySymbol { get; init; }
    public required KeyEventKind Kind { get; init; }
    public ModifierKeys Modifiers { get; init; }
}

public enum KeyEventKind { Press, Release }

[Flags]
public enum ModifierKeys
{
    None    = 0,
    Shift   = 1 << 0,
    Control = 1 << 1,
    Alt     = 1 << 2,
    /// <summary>The Windows/Command/Super key.</summary>
    Meta    = 1 << 3,
}

/// <summary>Pixel format of framebuffer data delivered by an <see cref="IFramebufferChannel"/>.</summary>
public enum FramebufferFormat { Bgra32, Rgba32, Rgb24 }
