using Avalonia;
using AwesomeAssertions;
using PolyTerm.App.Views;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers issue #363: on a screen smaller than the default window, the whole window, frame included,
/// has to end up inside the working area.
/// </summary>
public sealed class WindowFitTests
{
    private static readonly Size DefaultClient = new(1200, 800);
    private static readonly Size MinClient = new(800, 500);

    /// <summary>The Windows 11 frame at 100% scaling, as measured in the issue: 16x39.</summary>
    private static Size WithWindowsFrame(Size client) => new(client.Width + 16, client.Height + 39);

    [Fact]
    public void Calculate_WindowAlreadyFits_ReturnsNull()
    {
        var placement = WindowFit.Calculate(
            DefaultClient, WithWindowsFrame(DefaultClient), MinClient, new PixelRect(0, 0, 1920, 1032), 1);

        placement.Should().BeNull();
    }

    [Fact]
    public void Calculate_SmallScreen_PutsWholeFrameInsideWorkingArea()
    {
        // The issue's 1024x768 RDP session with a 48 px taskbar.
        var placement = WindowFit.Calculate(
            DefaultClient, WithWindowsFrame(DefaultClient), MinClient, new PixelRect(0, 0, 1024, 720), 1);

        placement.Should().Be(new WindowFit.Placement(new Size(1008, 681), new PixelPoint(0, 0)));
    }

    [Fact]
    public void Calculate_ClientFitsButFrameDoesNot_StillShrinks()
    {
        var placement = WindowFit.Calculate(
            DefaultClient, WithWindowsFrame(DefaultClient), MinClient, new PixelRect(0, 0, 1920, 820), 1);

        // Only the height needs to change, and the narrower window is centred.
        placement.Should().Be(new WindowFit.Placement(new Size(1200, 781), new PixelPoint(352, 0)));
    }

    [Fact]
    public void Calculate_ScaledScreen_ComparesInDipsAndPositionsInPixels()
    {
        // 1920x1080 at 150%: a 1280x672 DIP working area, too short for the 800 default.
        var placement = WindowFit.Calculate(
            DefaultClient, WithWindowsFrame(DefaultClient), MinClient, new PixelRect(0, 0, 1920, 1008), 1.5);

        placement.Should().Be(new WindowFit.Placement(new Size(1200, 633), new PixelPoint(48, 0)));
    }

    [Fact]
    public void Calculate_WorkingAreaNotAtOrigin_PositionsRelativeToIt()
    {
        // A second monitor to the left, with the taskbar along its top.
        var placement = WindowFit.Calculate(
            DefaultClient, WithWindowsFrame(DefaultClient), MinClient, new PixelRect(-1024, 48, 1024, 720), 1);

        placement!.Value.Position.Should().Be(new PixelPoint(-1024, 48));
    }

    [Fact]
    public void Calculate_ScreenBelowMinimumSize_KeepsMinimumAndPinsTopLeft()
    {
        var placement = WindowFit.Calculate(
            DefaultClient, WithWindowsFrame(DefaultClient), MinClient, new PixelRect(0, 0, 640, 440), 1);

        placement.Should().Be(new WindowFit.Placement(MinClient, new PixelPoint(0, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Calculate_NoUsableFrameSize_AllowsNoRoomForAFrame(bool smallerThanClient)
    {
        Size? frame = smallerThanClient ? new Size(1000, 700) : null;

        var placement = WindowFit.Calculate(
            DefaultClient, frame, MinClient, new PixelRect(0, 0, 1024, 720), 1);

        placement.Should().Be(new WindowFit.Placement(new Size(1024, 720), new PixelPoint(0, 0)));
    }
}
