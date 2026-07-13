using PolyTerm.Protocols.Serial;

namespace PolyTerm.App.Serial;

/// <summary>Tracks the currently detected USB COM ports, refreshing periodically while running (issue #33).</summary>
public interface IComPortWatcher
{
    /// <summary>The most recently detected set of USB COM ports. Empty before <see cref="ComPortWatcherService.Start"/>'s first poll completes.</summary>
    IReadOnlyList<DetectedComPort> CurrentPorts { get; }

    /// <summary>Fires whenever <see cref="CurrentPorts"/> changes to a different set (not on every poll tick — only on an actual change). Raised on a background thread, not the UI thread.</summary>
    event EventHandler? PortsChanged;
}
