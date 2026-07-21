namespace PolyTerm.Protocols.Ssh.X11;

/// <summary>
/// Platform-neutral access to PolyTerm's own vendored fallback X server (issue #76). The real
/// implementation, <see cref="BundledX11Server"/>, is Windows-only
/// (<c>[SupportedOSPlatform("windows")]</c>) — referencing it directly from platform-neutral code
/// (<c>SshSession</c>, <c>NewConnectionViewModel</c>) would trip the CA1416 platform-compatibility
/// analyzer there too, so this interface exists purely to give those call sites something to hold and
/// call without themselves becoming platform-gated. Registered against the real
/// <see cref="BundledX11Server"/> on Windows and <see cref="NullBundledX11ServerProvider"/> everywhere
/// else — same shape as <c>IComPortDetector</c>/<c>NullComPortDetector</c>.
/// </summary>
public interface IBundledX11ServerProvider
{
    /// <inheritdoc cref="BundledX11Server.IsAvailable"/>
    bool IsAvailable(out string? reason);

    /// <inheritdoc cref="BundledX11Server.EnsureRunningAsync"/>
    Task<X11DisplayEndpoint> EnsureRunningAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the bundled server if running. Same explicit lifecycle convention as
    /// <c>ComPortWatcherService</c>/<c>McpBridgeService</c> — driven from <c>App.axaml.cs</c>'s
    /// shutdown handler, not container disposal.
    /// </summary>
    Task StopAsync();
}

/// <summary>No-op used on every platform except Windows — see <see cref="IBundledX11ServerProvider"/>.</summary>
public sealed class NullBundledX11ServerProvider : IBundledX11ServerProvider
{
    public static readonly NullBundledX11ServerProvider Instance = new();
    private NullBundledX11ServerProvider() { }

    public bool IsAvailable(out string? reason)
    {
        reason = "Bundled X server is only available on Windows.";
        return false;
    }

    public Task<X11DisplayEndpoint> EnsureRunningAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Bundled X server is only available on Windows.");

    public Task StopAsync() => Task.CompletedTask;
}
