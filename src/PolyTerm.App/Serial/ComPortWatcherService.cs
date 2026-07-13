using Microsoft.Extensions.Logging;
using PolyTerm.Protocols.Serial;

namespace PolyTerm.App.Serial;

/// <summary>
/// Polls <see cref="IComPortDetector"/> on a fixed interval and raises <see cref="PortsChanged"/>
/// when the detected set actually changes. Polling rather than a Win32 device-change notification
/// (<c>WM_DEVICECHANGE</c>/<c>RegisterDeviceNotification</c>) — this app has no existing window-message
/// hook to attach one to, and a few seconds' latency on noticing a plugged-in USB adapter is an
/// acceptable tradeoff against the added complexity of a native message-loop integration for a
/// convenience/discovery feature. <see cref="Start"/>/<see cref="Stop"/>, not
/// <see cref="IDisposable"/>-driven cleanup, matching <c>McpBridgeService</c>'s own lifecycle
/// convention (driven explicitly from <c>App.axaml.cs</c>, not DI container disposal).
/// </summary>
public sealed class ComPortWatcherService : IComPortWatcher, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly IComPortDetector _detector;
    private readonly ILogger<ComPortWatcherService> _logger;
    private readonly object _lock = new();
    private Timer? _timer;
    private IReadOnlyList<DetectedComPort> _currentPorts = [];
    private int _polling;

    public IReadOnlyList<DetectedComPort> CurrentPorts { get { lock (_lock) return _currentPorts; } }

    public event EventHandler? PortsChanged;

    public ComPortWatcherService(IComPortDetector detector, ILogger<ComPortWatcherService> logger)
    {
        _detector = detector;
        _logger   = logger;
    }

    /// <summary>
    /// Starts polling. The first poll runs almost immediately (a zero due-time, not the full
    /// <see cref="PollInterval"/>) but asynchronously on a background thread — <see cref="CurrentPorts"/>
    /// stays empty for a brief moment at app startup rather than blocking the caller on a live WMI query.
    /// </summary>
    public void Start() => _timer = new Timer(Poll, null, TimeSpan.Zero, PollInterval);

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    /// <summary>Runs one poll cycle synchronously, right now, instead of waiting for the timer's next tick. Internal — used by tests to avoid waiting out the real <see cref="PollInterval"/>.</summary>
    internal void RefreshNow() => Poll(null);

    private void Poll(object? state)
    {
        // System.Threading.Timer doesn't wait for a callback to finish before scheduling the next one
        // - if a single WMI query ever takes longer than PollInterval (plausible under real load; WMI's
        // first query in a process has real service-handshake overhead), overlapping calls would stack
        // up rather than queue cleanly. Skip this tick entirely rather than let that happen - one poll
        // running behind by a few seconds is harmless for a discovery/convenience feature; concurrent
        // WMI queries piling up over a long-running process is not.
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
            return;

        try
        {
            IReadOnlyList<DetectedComPort> detected;
            try { detected = _detector.GetUsbPorts(); }
            catch (Exception ex)
            {
                // GetUsbPorts is documented as never-throwing, but this is a background timer callback -
                // an escaping exception here would go unobserved and silently kill future polls rather
                // than surfacing anywhere, so guard defensively regardless of that contract.
                _logger.LogDebug(ex, "COM port poll failed unexpectedly; keeping the previous known set.");
                return;
            }

            bool changed;
            lock (_lock)
            {
                changed = !SameContents(_currentPorts, detected);
                if (changed) _currentPorts = detected;
            }
            if (changed)
                PortsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static bool SameContents(IReadOnlyList<DetectedComPort> a, IReadOnlyList<DetectedComPort> b) =>
        a.Count == b.Count && a.ToHashSet().SetEquals(b);
}
