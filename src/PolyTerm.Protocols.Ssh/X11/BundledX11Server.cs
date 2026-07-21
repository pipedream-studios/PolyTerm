using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Protocols.Ssh.X11;

/// <summary>
/// Manages the lifecycle of PolyTerm's own vendored X server (issue #76, M5 — a maintained VcXsrv
/// fork) on Windows, as a fallback for when no user-installed X server (VcXsrv/Xming/X410 et al.,
/// see <see cref="X11ServerGuidance"/>) is reachable. Windows-only: Linux has a native X session and
/// macOS has XQuartz, so bundling only makes sense where the platform has no X server story of its
/// own. One instance is meant to live for the app's lifetime and be shared across sessions — a real X
/// server is long-lived infrastructure, not a per-connection resource, matching how a user's own
/// VcXsrv normally sits in the tray for the whole desktop session.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BundledX11Server : IAsyncDisposable
{
    private static readonly string DefaultExePath = Path.Combine(AppContext.BaseDirectory, "native", "vcxsrv", "vcxsrv.exe");

    private readonly ILogger _logger;
    private readonly string _exePath;
    private readonly TimeSpan _readinessPollInterval;
    private readonly int _readinessPollAttempts;
    private Process? _process;
    private int? _displayNumber;

    /// <summary>Test-only observation seam: lets tests confirm a second <see cref="EnsureRunningAsync"/> call reused the same process rather than spawning another.</summary>
    internal int? ProcessId => _process?.Id;

    public BundledX11Server(ILogger logger) : this(logger, DefaultExePath) { }

    /// <summary>Test-only seam: points at a fake exe path instead of the real vendored one, which only exists in a packaged build.</summary>
    internal BundledX11Server(ILogger logger, string exePath) : this(logger, exePath, TimeSpan.FromMilliseconds(100), 50) { }

    /// <summary>Test-only seam: also shrinks the "wait for the process to start listening" poll so tests exercising a process that never actually listens (a stand-in exe, not real VcXsrv) don't have to eat the full production timeout.</summary>
    internal BundledX11Server(ILogger logger, string exePath, TimeSpan readinessPollInterval, int readinessPollAttempts)
    {
        _logger = logger;
        _exePath = exePath;
        _readinessPollInterval = readinessPollInterval;
        _readinessPollAttempts = readinessPollAttempts;
    }

    /// <summary>
    /// Whether the vendored X server is present in this build. False whenever <c>native/vcxsrv/</c>
    /// wasn't staged into the output — the packaging-optional case (see #67/#84's FreeRDP shim for the
    /// same "pack-optional, capability-gated" containment rule), not an error.
    /// </summary>
    public bool IsAvailable(out string? reason)
    {
        if (File.Exists(_exePath))
        {
            reason = null;
            return true;
        }
        reason = "Bundled X server not found — this build wasn't packaged with the vendored VcXsrv component.";
        return false;
    }

    /// <summary>
    /// Starts the bundled server if it isn't already running, picking the first free display number,
    /// and returns the endpoint to bridge X11-forwarded connections to. Idempotent: a second call
    /// while the process is still alive returns the same endpoint without starting another instance.
    /// </summary>
    public async Task<X11DisplayEndpoint> EnsureRunningAsync(CancellationToken ct = default)
    {
        if (_process is { HasExited: false } && _displayNumber is int running)
            return LoopbackEndpoint(running);

        if (!IsAvailable(out var reason))
            throw new InvalidOperationException(reason);

        var displayNumber = FindFreeDisplayNumber();
        var startInfo = new ProcessStartInfo
        {
            FileName = _exePath,
            // -multiwindow: X apps get their own top-level Windows windows rather than one big
            // fixed-size desktop, matching how VcXsrv is normally run for exactly this use case.
            // -clipboard: matches ssh -X's expected clipboard-sharing behavior. -silent-dup-error:
            // don't pop a GUI error dialog if the display number races with something else - we
            // already probed it above, but this is belt-and-suspenders, not the primary guard.
            Arguments = $":{displayNumber} -multiwindow -clipboard -silent-dup-error",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Starting bundled X server on display :{DisplayNumber}.", displayNumber);
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the bundled X server process.");
        _displayNumber = displayNumber;

        var endpoint = LoopbackEndpoint(displayNumber);
        // Give the freshly-spawned process a moment to actually open its listening socket before
        // reporting it ready - Process.Start returning only means the OS scheduled it, not that
        // VcXsrv has finished its own startup.
        for (var attempt = 0; attempt < _readinessPollAttempts && !endpoint.IsReachable(_readinessPollInterval); attempt++)
            await Task.Delay(_readinessPollInterval, ct).ConfigureAwait(false);

        return endpoint;
    }

    private static X11DisplayEndpoint LoopbackEndpoint(int displayNumber) =>
        X11DisplayEndpoint.Resolve($"127.0.0.1:{displayNumber}", isWindows: true);

    /// <summary>
    /// Reuses <see cref="X11DisplayEndpoint.IsReachable"/> - the same probe already used to decide
    /// whether a user-installed server is running - to find a display number nothing is listening on
    /// yet, so the bundled server never collides with a real VcXsrv/Xming/X410 the user happens to
    /// already have running.
    /// </summary>
    private static int FindFreeDisplayNumber()
    {
        const int maxDisplayNumber = 100;
        for (var candidate = 0; candidate < maxDisplayNumber; candidate++)
        {
            if (!LoopbackEndpoint(candidate).IsReachable(TimeSpan.FromMilliseconds(50)))
                return candidate;
        }
        throw new InvalidOperationException($"No free X display number found in 0-{maxDisplayNumber - 1}.");
    }

    /// <summary>Stops the bundled server, if running. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_process is not { HasExited: false } process)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping the bundled X server process.");
        }
        finally
        {
            process.Dispose();
            _process = null;
            _displayNumber = null;
        }
    }
}
