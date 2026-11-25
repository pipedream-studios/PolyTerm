using Avalonia;

namespace PolyTerm.App.Views;

/// <summary>
/// Works out how to bring a window that's too big for its screen back inside the working area,
/// counting its frame (title bar and borders) as well as its client area (issue #363). Kept apart
/// from <see cref="MainWindow"/> because the headless test platform has one fixed 1920x1280 screen
/// and reports no frame, so it can't reproduce a small screen.
/// </summary>
internal static class WindowFit
{
    /// <summary>The client size to give the window (DIPs) and where to put its frame (physical pixels).</summary>
    internal readonly record struct Placement(Size ClientSize, PixelPoint Position);

    /// <summary>
    /// Returns the new placement, or null if the window already fits and should be left alone.
    /// </summary>
    /// <param name="clientSize">Current client size, in DIPs.</param>
    /// <param name="frameSize">
    /// Current size including the frame, in DIPs, or null if the platform can't say yet, in which
    /// case no room is allowed for the frame.
    /// </param>
    /// <param name="minClientSize">
    /// The client area is never made smaller than this, so on a very small screen the window can
    /// still overflow to the right and bottom.
    /// </param>
    /// <param name="workingArea">The screen's working area (excluding the taskbar), in physical pixels.</param>
    /// <param name="scaling">The screen's scaling factor: physical pixels per DIP.</param>
    internal static Placement? Calculate(
        Size clientSize, Size? frameSize, Size minClientSize, PixelRect workingArea, double scaling)
    {
        var frame = frameSize ?? clientSize;
        var decorationWidth  = Math.Max(0, frame.Width  - clientSize.Width);
        var decorationHeight = Math.Max(0, frame.Height - clientSize.Height);

        var maxWidth  = workingArea.Width  / scaling - decorationWidth;
        var maxHeight = workingArea.Height / scaling - decorationHeight;
        if (clientSize.Width <= maxWidth && clientSize.Height <= maxHeight)
            return null;

        var fitted = new Size(
            Math.Max(minClientSize.Width,  Math.Min(clientSize.Width,  maxWidth)),
            Math.Max(minClientSize.Height, Math.Min(clientSize.Height, maxHeight)));

        // Centre the frame. If it's still too big, pin its top-left to the working area's so the
        // title bar stays reachable.
        var frameWidth  = (int)Math.Round((fitted.Width  + decorationWidth)  * scaling);
        var frameHeight = (int)Math.Round((fitted.Height + decorationHeight) * scaling);
        var position = new PixelPoint(
            workingArea.X + Math.Max(0, (workingArea.Width  - frameWidth)  / 2),
            workingArea.Y + Math.Max(0, (workingArea.Height - frameHeight) / 2));

        return new Placement(fitted, position);
    }
}
