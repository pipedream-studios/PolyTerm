using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Terminal;
using PolyTerm.App.ViewModels;
using PolyTerm.App.Views;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers copy on select (issue #346) with real pointer input on a headless <see cref="TerminalSurface"/>, and
/// the app setting reaching a surface that is already open. Saving the setting writes settings.json, so
/// POLYTERM_DATA_DIR points at a temporary directory for the duration.
/// </summary>
[Collection("AppSettings")]
public sealed class TerminalCopyOnSelectTests : IDisposable
{
    private const string Untouched = "already on the clipboard";

    private readonly bool _originalSetting = App.Settings.TerminalCopyOnSelect;
    private readonly string? _originalDataDir = Environment.GetEnvironmentVariable("POLYTERM_DATA_DIR");
    private readonly string _dataDir = Directory.CreateTempSubdirectory("polyterm-copy-on-select-").FullName;

    public TerminalCopyOnSelectTests() => Environment.SetEnvironmentVariable("POLYTERM_DATA_DIR", _dataDir);

    public void Dispose()
    {
        App.Settings.TerminalCopyOnSelect = _originalSetting;
        Environment.SetEnvironmentVariable("POLYTERM_DATA_DIR", _originalDataDir);
        Directory.Delete(_dataDir, recursive: true);
    }

    private sealed class FakeTerminalChannel : ITerminalChannel
    {
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public string TerminalType => "xterm-256color";
        public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default) => ValueTask.CompletedTask;
        public void Emit(string text) => OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));
    }

    private sealed class Terminal : IDisposable
    {
        private readonly Window _window;
        private readonly double _cellWidth, _cellHeight;

        public Terminal(TerminalSurface surface)
        {
            var channel = new FakeTerminalChannel();
            surface.Channel = channel;
            _window = new Window { Width = 800, Height = 600, Content = surface };
            _window.Show();
            Dispatcher.UIThread.RunJobs();
            channel.Emit("hello world\r\n");

            // The same measurement the surface uses to map pixels to cells.
            var cell = new FormattedText("M", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(surface.TerminalFont), surface.FontSize, Brushes.White);
            (_cellWidth, _cellHeight) = (cell.Width, cell.Height);
        }

        private Point Cell(int col, int row) => new((col + 0.5) * _cellWidth, (row + 0.5) * _cellHeight);

        public void Drag(int fromCol, int toCol, int row = 0)
        {
            _window.MouseDown(Cell(fromCol, row), MouseButton.Left);
            _window.MouseMove(Cell(toCol, row), RawInputModifiers.LeftMouseButton);
            _window.MouseUp(Cell(toCol, row), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Click(int col, MouseButton button)
        {
            _window.MouseDown(Cell(col, 0), button);
            _window.MouseUp(Cell(col, 0), button);
            Dispatcher.UIThread.RunJobs();
        }

        public Task SetClipboardAsync(string text) => _window.Clipboard!.SetTextAsync(text);

        public Task<string?> ClipboardAsync() => _window.Clipboard!.TryGetTextAsync();

        public void Dispose() => _window.Close();
    }

    [AvaloniaFact]
    public async Task Off_ByDefault_DragDoesNotTouchTheClipboard()
    {
        new AppSettings().TerminalCopyOnSelect.Should().BeFalse();
        using var terminal = new Terminal(new TerminalSurface());
        await terminal.SetClipboardAsync(Untouched);

        terminal.Drag(0, 4);

        (await terminal.ClipboardAsync()).Should().Be(Untouched);
    }

    [AvaloniaTheory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    public async Task On_DragInEitherDirection_CopiesTheSelectedText(int from, int to)
    {
        using var terminal = new Terminal(new TerminalSurface { CopyOnSelect = true });
        await terminal.SetClipboardAsync(Untouched);

        terminal.Drag(from, to);

        (await terminal.ClipboardAsync()).Should().Be("hello");
    }

    [AvaloniaFact]
    public void Copying_ReportsTheCharacterCount_ToAncestorsOfTheSurface()
    {
        var surface = new TerminalSurface { CopyOnSelect = true };
        using var terminal = new Terminal(surface);
        var counts = new List<int>();
        // On the parent window, as MainWindow listens.
        TopLevel.GetTopLevel(surface)!.AddHandler(TerminalSurface.TextCopiedEvent, (_, e) => counts.Add(e.CharacterCount));

        terminal.Drag(0, 4);
        terminal.Click(2, MouseButton.Left);
        terminal.Drag(2, 8, row: 3);

        counts.Should().Equal([5], "only the drag over text copied anything");
    }

    [AvaloniaFact]
    public async Task On_ClickWithoutDragging_LeavesTheClipboardAlone()
    {
        using var terminal = new Terminal(new TerminalSurface { CopyOnSelect = true });
        await terminal.SetClipboardAsync(Untouched);

        terminal.Click(2, MouseButton.Left);

        (await terminal.ClipboardAsync()).Should().Be(Untouched);
    }

    [AvaloniaFact]
    public async Task On_DragOverBlankCells_LeavesTheClipboardAlone()
    {
        using var terminal = new Terminal(new TerminalSurface { CopyOnSelect = true });
        await terminal.SetClipboardAsync(Untouched);

        terminal.Drag(2, 8, row: 3);

        (await terminal.ClipboardAsync()).Should().Be(Untouched);
    }

    [AvaloniaFact]
    public async Task On_RightClickOnAnExistingSelection_DoesNotCopyItAgain()
    {
        // No context menu: the bare headless app has no theme to host its popup.
        using var terminal = new Terminal(new TerminalSurface { CopyOnSelect = true, ContextMenu = null });
        terminal.Drag(0, 4);
        await terminal.SetClipboardAsync(Untouched);

        terminal.Click(2, MouseButton.Right);

        (await terminal.ClipboardAsync()).Should().Be(Untouched);
    }

    [AvaloniaFact]
    public async Task ChangingTheSetting_AppliesToAnOpenTab_AndKeepsItsZoom()
    {
        App.Settings.TerminalCopyOnSelect = false;
        var surface = (TerminalSurface)new SessionSurfaceTemplateSelector().Build(null);
        using var terminal = new Terminal(surface);
        var zoomed = surface.FontSize + 4;
        surface.FontSize = zoomed;

        App.SetTerminalCopyOnSelect(true);

        surface.CopyOnSelect.Should().BeTrue();
        surface.FontSize.Should().Be(zoomed);

        App.SetTerminalCopyOnSelect(false);
        await terminal.SetClipboardAsync(Untouched);

        surface.CopyOnSelect.Should().BeFalse();
        File.ReadAllText(Path.Combine(_dataDir, "settings.json")).Should().Contain("\"terminalCopyOnSelect\":false");
    }

    [AvaloniaFact]
    public void ANewTab_StartsWithTheSavedSetting()
    {
        App.Settings.TerminalCopyOnSelect = true;

        var surface = (TerminalSurface)new SessionSurfaceTemplateSelector().Build(null);

        surface.CopyOnSelect.Should().BeTrue();
    }
}
