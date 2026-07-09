namespace PolyTerm.App.McpBridge;

/// <summary>
/// Per-connection sliding-window call-rate limiter for the MCP bridge (spec §4.7 design rule 4:
/// "a local policy knob, not a monetization lever" — generous by default, no artificial daily cap).
/// One instance is created per pipe connection (see <c>McpBridgeService.HandleConnectionAsync</c>),
/// so a busy MCP client can't starve another one connected at the same time.
/// </summary>
internal sealed class McpRateLimiter
{
    private readonly Queue<DateTime> _callTimestamps = new();
    private readonly object _lock = new();

    /// <summary>
    /// Records a call attempt now and returns whether it's within <paramref name="maxCallsPerMinute"/>
    /// (a sliding 60-second window, not a fixed per-minute bucket that resets on the clock). A limit
    /// of 0 or less disables the check entirely (unlimited).
    /// </summary>
    public bool TryAcquire(int maxCallsPerMinute)
    {
        if (maxCallsPerMinute <= 0) return true;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddMinutes(-1);
            while (_callTimestamps.Count > 0 && _callTimestamps.Peek() < cutoff)
                _callTimestamps.Dequeue();

            if (_callTimestamps.Count >= maxCallsPerMinute)
                return false;

            _callTimestamps.Enqueue(now);
            return true;
        }
    }
}
