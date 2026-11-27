using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Framebuffer;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Issue #368: whatever <see cref="FramebufferSurface"/> has sent to the remote as down must be
/// released when the real release isn't going to reach it. Drives a live surface in a headless window
/// and checks what arrives at the channel. Uses Shift, Control and Meta only: Alt is judged on macOS
/// alone, and these tests run on every OS.
/// </summary>
public sealed class FramebufferSurfaceInputTests
{
    private static readonly Point Middle = new(50, 50);

    [AvaloniaFact]
    public void LosingFocus_ReleasesHeldKeys()
    {
        var (window, surface, channel, other) = Show();
        window.KeyPressQwerty(PhysicalKey.MetaLeft, RawInputModifiers.Meta);

        other.Focus();

        channel.Sent.Should().Equal("press Super_L", "release Super_L");
        window.Close();
    }

    [AvaloniaFact]
    public void DeactivatingTheWindow_ReleasesHeldKeys_ThoughTheSurfaceKeepsFocus()
    {
        var (window, surface, channel, _) = Show();
        window.KeyPressQwerty(PhysicalKey.MetaLeft, RawInputModifiers.Meta);

        Deactivate(window);

        surface.IsFocused.Should().BeTrue();
        channel.Sent.Should().Equal("press Super_L", "release Super_L");
        window.Close();
    }

    [AvaloniaFact]
    public void DetachingTheSurface_ReleasesHeldKeys()
    {
        var (window, _, channel, _) = Show();
        window.KeyPressQwerty(PhysicalKey.ControlLeft, RawInputModifiers.Control);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        channel.Sent.Should().Equal("press Control_L", "release Control_L");
        window.Close();
    }

    [AvaloniaFact]
    public void ChangingChannel_ReleasesHeldKeysOnTheOldOne()
    {
        var (window, surface, channel, _) = Show();
        window.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Shift);
        var next = new RecordingChannel();

        surface.Channel = next;

        channel.Sent.Should().Equal("press Shift_L", "release Shift_L");
        next.Sent.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(MouseButton.Left, "Left")]
    [InlineData(MouseButton.Right, "Right")]
    public void AClickWithoutTheHeldModifier_ReleasesItBeforeTheClickIsSent(MouseButton button, string name)
    {
        var (window, _, channel, _) = Show();
        window.KeyPressQwerty(PhysicalKey.MetaLeft, RawInputModifiers.Meta);

        window.MouseDown(Middle, button, RawInputModifiers.None);
        window.MouseUp(Middle, button, RawInputModifiers.None);

        channel.SentExceptMoves
            .Should().Equal("press Super_L", "release Super_L", $"press {name}", $"release {name}");
        window.Close();
    }

    [AvaloniaFact]
    public void AClickWithTheModifierStillDown_LeavesItHeld()
    {
        var (window, _, channel, _) = Show();
        window.KeyPressQwerty(PhysicalKey.MetaLeft, RawInputModifiers.Meta);

        window.MouseDown(Middle, MouseButton.Left, RawInputModifiers.Meta);
        window.MouseUp(Middle, MouseButton.Left, RawInputModifiers.Meta);

        channel.SentExceptMoves.Should().Equal("press Super_L", "press Left", "release Left");
        window.Close();
    }

    [AvaloniaFact]
    public void MovingTheMouseWithoutTheHeldModifier_ReleasesIt()
    {
        var (window, _, channel, _) = Show();
        window.KeyPressQwerty(PhysicalKey.MetaLeft, RawInputModifiers.Meta);

        window.MouseMove(Middle, RawInputModifiers.None);

        channel.Sent.Should().Equal("press Super_L", "release Super_L", "move None");
        window.Close();
    }

    [AvaloniaFact]
    public void AnotherKeyWithoutTheHeldModifier_ReleasesItFirst()
    {
        var (window, _, channel, _) = Show();
        window.KeyPressQwerty(PhysicalKey.MetaLeft, RawInputModifiers.Meta);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        channel.Sent.Should().Equal("press Super_L", "release Super_L", "press Return");
        window.Close();
    }

    /// <summary>X11 reports the modifier state from before the event, so a second modifier's press lacks its own flag.</summary>
    [AvaloniaFact]
    public void PressingAModifier_DoesNotJudgeWhatIsHeld()
    {
        var (window, _, channel, _) = Show();
        window.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.None);

        window.KeyPressQwerty(PhysicalKey.ControlLeft, RawInputModifiers.Shift);

        channel.Sent.Should().Equal("press Shift_L", "press Control_L");
        window.Close();
    }

    /// <summary>macOS raises one key-up for a pair, once both keys are up, naming only the last one released.</summary>
    [AvaloniaFact]
    public void OneKeyUpForAPairOfModifiers_ReleasesBoth()
    {
        var (window, _, channel, _) = Show();
        window.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Shift);
        window.KeyPressQwerty(PhysicalKey.ShiftRight, RawInputModifiers.Shift);

        window.KeyReleaseQwerty(PhysicalKey.ShiftRight, RawInputModifiers.None);

        channel.Sent.Should().Equal("press Shift_L", "press Shift_R", "release Shift_R", "release Shift_L");
        window.Close();
    }

    [AvaloniaFact]
    public void ReleasingOneOfAPair_WhileTheOtherIsDown_LeavesTheOtherHeld()
    {
        var (window, _, channel, _) = Show();
        window.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Shift);
        window.KeyPressQwerty(PhysicalKey.ShiftRight, RawInputModifiers.Shift);

        window.KeyReleaseQwerty(PhysicalKey.ShiftRight, RawInputModifiers.Shift);

        channel.Sent.Should().Equal("press Shift_L", "press Shift_R", "release Shift_R");
        window.Close();
    }

    [AvaloniaFact]
    public void LosingPointerCapturePartwayThroughAClick_ReleasesTheButton()
    {
        var (window, surface, channel, _) = Show();
        IPointer? pointer = null;
        surface.PointerPressed += (_, e) => pointer = e.Pointer;
        window.MouseDown(Middle, MouseButton.Left, RawInputModifiers.None);

        pointer!.Capture(null);

        channel.SentExceptMoves.Should().Equal("press Left", "release Left");
        window.Close();
    }

    [AvaloniaFact]
    public void AnOrdinaryClick_SendsOneRelease()
    {
        var (window, _, channel, _) = Show();

        window.MouseDown(Middle, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(Middle, MouseButton.Left, RawInputModifiers.None);

        channel.SentExceptMoves.Should().Equal("press Left", "release Left");
        window.Close();
    }

    private static (Window Window, FramebufferSurface Surface, RecordingChannel Channel, Button Other) Show()
    {
        var channel = new RecordingChannel();
        var surface = new FramebufferSurface { Width = 100, Height = 100, Channel = channel };
        var other = new Button { Width = 100, Height = 20 };
        var window = new Window { Width = 100, Height = 120, Content = new StackPanel { Children = { surface, other } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        channel.PaintFrame(); // the surface only hit-tests where it has drawn something
        Dispatcher.UIThread.RunJobs();
        surface.Focus();
        surface.InvalidateVisual();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        channel.Sent.Clear();
        return (window, surface, channel, other);
    }

    // Headless windows never deactivate themselves, so raise the platform's callback directly. Avalonia
    // doesn't let code outside it use that property, hence the reflection.
    private static void Deactivate(Window window)
    {
        var platform = window.PlatformImpl!;
        var deactivated = (Action?)platform.GetType().GetProperty("Deactivated")!.GetValue(platform);
        deactivated!.Invoke();
    }

    private sealed class RecordingChannel : IFramebufferChannel
    {
        public List<string> Sent { get; } = [];
        public IEnumerable<string> SentExceptMoves => Sent.Where(s => !s.StartsWith("move", StringComparison.Ordinal));

        public int Width => 100;
        public int Height => 100;
        public FramebufferFormat Format => FramebufferFormat.Rgba32;
        public event EventHandler<FramebufferRegionUpdatedEventArgs>? RegionUpdated;
        public bool SharesClipboard => false;
        public event EventHandler<ClipboardPayload>? ClipboardReceived { add { } remove { } }

        public void PaintFrame() => RegionUpdated?.Invoke(this,
            new FramebufferRegionUpdatedEventArgs { X = 0, Y = 0, Width = Width, Height = Height, Data = new byte[Width * Height * 4] });

        public ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default)
        {
            Sent.Add($"{e.Kind.ToString().ToLowerInvariant()} {e.Button}");
            return ValueTask.CompletedTask;
        }

        public ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default)
        {
            Sent.Add($"{e.Kind.ToString().ToLowerInvariant()} {e.KeySymbol}");
            return ValueTask.CompletedTask;
        }

        public ValueTask SendCtrlAltDeleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendClipboardAsync(ClipboardPayload payload, CancellationToken ct = default) => ValueTask.CompletedTask;
        public byte[]? CaptureSnapshot() => null;
    }
}
