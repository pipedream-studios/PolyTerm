using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Framebuffer;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Surfaces.Tests;

/// <summary>
/// Issue #364: switching tabs left RDP and VNC views blank. Updates only cover what changes on the remote,
/// so a view has to start from the current frame. These check the pixels a surface actually renders.
/// </summary>
public sealed class FramebufferSurfaceRenderingTests
{
    private const int Size = 100;

    private static (Window Window, Panel Panel, FramebufferSurface Surface) Show(IFramebufferChannel channel)
    {
        var surface = new FramebufferSurface { Channel = channel };
        var panel = new Panel { Children = { surface } };
        var window = new Window { Width = Size, Height = Size, Content = panel };
        window.Show();
        return (window, panel, surface);
    }

    private static Color PixelAt(Window window, FramebufferSurface surface, int x, int y)
    {
        Dispatcher.UIThread.RunJobs(); // applies posted updates
        surface.InvalidateVisual();    // rather than waiting for the surface's render timer
        using var frame = window.CaptureRenderedFrame()!;
        using var pixels = frame.Lock();
        var offset = y * pixels.RowBytes + x * 4;
        var first = Marshal.ReadByte(pixels.Address, offset);
        var green = Marshal.ReadByte(pixels.Address, offset + 1);
        var third = Marshal.ReadByte(pixels.Address, offset + 2);
        // The captured frame's channel order depends on the OS.
        return pixels.Format == Avalonia.Platform.PixelFormat.Bgra8888
            ? Color.FromRgb(third, green, first)
            : Color.FromRgb(first, green, third);
    }

    [AvaloniaFact]
    public void ANewViewOfARunningSession_ShowsTheWholeDesktopBeforeAnyUpdate()
    {
        var channel = new FrameChannel();
        channel.ChangeUnannounced(Colors.Red);

        var (window, _, surface) = Show(channel);

        PixelAt(window, surface, 10, 10).Should().Be(Colors.Red);
        PixelAt(window, surface, 90, 90).Should().Be(Colors.Red);
        window.Close();
    }

    [AvaloniaFact]
    public void AfterTheFirstPicture_AnUpdateRepaintsOnlyItsRegion()
    {
        var channel = new FrameChannel();
        channel.ChangeUnannounced(Colors.Red);
        var (window, _, surface) = Show(channel);

        channel.Update(0, 0, 20, 20, Colors.Blue);

        PixelAt(window, surface, 10, 10).Should().Be(Colors.Blue);
        PixelAt(window, surface, 90, 90).Should().Be(Colors.Red);
        window.Close();
    }

    [AvaloniaFact]
    public void AViewPutBackOnScreen_ShowsTheCurrentDesktop_AndCarriesOnUpdating()
    {
        var channel = new FrameChannel();
        channel.ChangeUnannounced(Colors.Red);
        var (window, panel, surface) = Show(channel);

        panel.Children.Remove(surface);
        Dispatcher.UIThread.RunJobs();
        channel.Listeners.Should().Be(0, "a view that's off screen shouldn't be sent updates");
        channel.ChangeUnannounced(Colors.Lime);
        panel.Children.Add(surface);

        channel.Listeners.Should().Be(1);
        PixelAt(window, surface, 90, 90).Should().Be(Colors.Lime);
        channel.Update(0, 0, 20, 20, Colors.Blue);
        PixelAt(window, surface, 10, 10).Should().Be(Colors.Blue);
        PixelAt(window, surface, 90, 90).Should().Be(Colors.Lime);
        window.Close();
    }

    [AvaloniaFact]
    public void BeforeTheFirstFrameArrives_TheViewWaitsForAnUpdate()
    {
        var channel = new FrameChannel(); // no snapshot yet
        var (window, _, surface) = Show(channel);

        channel.Update(0, 0, Size, Size, Colors.Blue);

        PixelAt(window, surface, 50, 50).Should().Be(Colors.Blue);
        window.Close();
    }

    [AvaloniaFact]
    public void ARegionThatNoLongerFitsTheDesktop_IsDropped()
    {
        var channel = new FrameChannel();
        channel.ChangeUnannounced(Colors.Red);
        var (window, _, surface) = Show(channel);

        channel.Update(90, 0, 20, Size, Colors.Blue); // as if sent before the desktop shrank

        PixelAt(window, surface, 95, 50).Should().Be(Colors.Red);
        PixelAt(window, surface, 5, 51).Should().Be(Colors.Red);
        window.Close();
    }

    private sealed class FrameChannel : IFramebufferChannel
    {
        private byte[]? _frame;

        public int Width => Size;
        public int Height => Size;
        public FramebufferFormat Format => FramebufferFormat.Rgba32;
        public event EventHandler<FramebufferRegionUpdatedEventArgs>? RegionUpdated;
        public int Listeners => RegionUpdated?.GetInvocationList().Length ?? 0;
        public bool SharesClipboard => false;
        public event EventHandler<ClipboardPayload>? ClipboardReceived { add { } remove { } }

        /// <summary>Changes the whole desktop without raising an update, so only a snapshot shows it.</summary>
        public void ChangeUnannounced(Color colour) => _frame = Solid(Size, Size, colour);

        /// <summary>Raises an update for one region. It doesn't change the snapshot.</summary>
        public void Update(int x, int y, int width, int height, Color colour) =>
            RegionUpdated?.Invoke(this, new FramebufferRegionUpdatedEventArgs
            {
                X = x, Y = y, Width = width, Height = height, Data = Solid(width, height, colour),
            });

        private static byte[] Solid(int width, int height, Color colour)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (colour.R, colour.G, colour.B, 255);
            return pixels;
        }

        public byte[]? CaptureSnapshot() => (byte[]?)_frame?.Clone();
        public ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendCtrlAltDeleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendClipboardAsync(ClipboardPayload payload, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
