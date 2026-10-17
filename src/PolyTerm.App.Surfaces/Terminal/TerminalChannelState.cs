using System.Runtime.CompilerServices;
using PolyTerm.Engine;
using PolyTerm.Terminal;

namespace PolyTerm.App.Surfaces.Terminal;

/// <summary>
/// The emulator for one <see cref="ITerminalChannel"/>, owned by the channel rather than by any
/// <see cref="TerminalSurface"/>. The tab control destroys a tab's surface when another tab is selected, so
/// keeping the emulator here means switching back restores the screen and scrollback, and output that
/// arrives while the tab is in the background is still fed in.
/// </summary>
internal sealed class TerminalChannelState
{
    private static readonly ConditionalWeakTable<ITerminalChannel, TerminalChannelState> s_states = new();

    /// <summary>Guards <see cref="Emulator"/>: it is fed on the channel's read thread but read from the UI thread.</summary>
    public readonly object Lock = new();

    public TerminalEmulator Emulator { get; }

    /// <summary>Raised after output is fed, on the feeding thread and under <see cref="Lock"/>; the argument is how many lines were pushed into scrollback.</summary>
    public event Action<int>? Fed;

    /// <summary>Raised when the screen changed (any thread).</summary>
    public event Action? ScreenUpdated;

    /// <summary>Raised when the remote sets an OSC title (any thread).</summary>
    public event Action<string>? TitleChanged;

    private TerminalChannelState(ITerminalChannel channel, int cols, int rows)
    {
        Emulator = new TerminalEmulator(cols, rows, 5000,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TerminalEmulator>.Instance);
        Emulator.ScreenUpdated += (_, _) => ScreenUpdated?.Invoke();
        Emulator.TitleChanged  += (_, title) => TitleChanged?.Invoke(title);
        channel.OutputReceived += OnOutput;
    }

    /// <summary>The state for <paramref name="channel"/>, created at <paramref name="cols"/>×<paramref name="rows"/> on first use.</summary>
    public static TerminalChannelState For(ITerminalChannel channel, int cols, int rows) =>
        s_states.GetValue(channel, c => new TerminalChannelState(c, cols, rows));

    private void OnOutput(object? sender, ReadOnlyMemory<byte> data)
    {
        lock (Lock)
        {
            var before = Emulator.ScrollbackLineCount;
            Emulator.Feed(data.Span);
            Fed?.Invoke(Emulator.ScrollbackLineCount - before);
        }
    }
}
