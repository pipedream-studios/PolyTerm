using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Protocols.Ssh.X11;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>
/// Real, live coverage of <see cref="BundledX11Server"/> (issue #76) - not against a real VcXsrv build
/// (that only exists in a packaged Windows build once #76's vendoring/CI step ships), but against a
/// throwaway <c>.cmd</c> stand-in process that ignores the vcxsrv-style arguments and just stays alive
/// until killed, so the actual process-lifecycle code (start/reuse/kill) runs for real rather than
/// being mocked. Confirmed empirically that `Process.Start` with `UseShellExecute = false` launches a
/// `.cmd` directly on Windows (OS-level association, no shell needed) before relying on it here. Skips
/// on non-Windows - this class is `[SupportedOSPlatform("windows")]`, matching the containment rule
/// (#76 bundling is a Windows-only fallback; Linux/macOS already have their own X server story).
/// </summary>
public sealed class BundledX11ServerTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "polyterm-bundled-x11-test-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromMilliseconds(20);
    private const int FastPollAttempts = 5;

    private const string SkipReason = "BundledX11Server is [SupportedOSPlatform(\"windows\")] - bundling only makes sense where the platform has no native X server story of its own.";

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    private string CreateStandInExe()
    {
        Directory.CreateDirectory(_workDir);
        var path = Path.Combine(_workDir, "standin.cmd");
        // Ignores whatever vcxsrv-style arguments BundledX11Server passes it and just stays alive
        // (a long ping, not `timeout` - timeout needs a real console for input, which a test host
        // may not have) until the test kills it via DisposeAsync.
        File.WriteAllText(path, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n");
        return path;
    }

    /// <summary>
    /// Pins down that the public constructor's real default path
    /// (<c>AppContext.BaseDirectory/native/vcxsrv/vcxsrv.exe</c>) matches exactly where
    /// PolyTerm.Protocols.Ssh.csproj's packaging wiring (issue #76) lands the fetched tree - the two
    /// are defined independently (a hardcoded <c>Path.Combine</c> in this class vs. an MSBuild
    /// <c>Link=</c> pattern in the csproj) and nothing else would catch them silently drifting apart.
    /// Doesn't assert a specific true/false outcome - a real checkout normally has no vendored tree at
    /// all (nothing commits it, it's fetched separately by .github/workflows/fetch-vcxsrv-windows.yml)
    /// - only that <see cref="BundledX11Server.IsAvailable"/>'s answer is consistent with whatever's
    /// actually on disk at that exact path.
    /// </summary>
    [Fact]
    public void IsAvailable_UsingRealDefaultConstructor_MatchesWhatsActuallyOnDiskAtThePackagedPath()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var expectedPath = Path.Combine(AppContext.BaseDirectory, "native", "vcxsrv", "vcxsrv.exe");
        var server = new BundledX11Server(NullLogger.Instance);

        server.IsAvailable(out _).Should().Be(File.Exists(expectedPath));
    }

    [Fact]
    public void IsAvailable_WhenExeMissing_ReturnsFalseWithReason()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var server = new BundledX11Server(NullLogger.Instance, Path.Combine(_workDir, "does-not-exist.exe"));

        server.IsAvailable(out var reason).Should().BeFalse();
        reason.Should().NotBeNullOrEmpty("callers need a user-facing explanation for why the bundled-server fallback isn't offered");
    }

    [Fact]
    public void IsAvailable_WhenExePresent_ReturnsTrueWithNoReason()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var server = new BundledX11Server(NullLogger.Instance, CreateStandInExe());

        server.IsAvailable(out var reason).Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public async Task EnsureRunningAsync_WhenExeMissing_Throws()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var server = new BundledX11Server(NullLogger.Instance, Path.Combine(_workDir, "does-not-exist.exe"), FastPollInterval, FastPollAttempts);

        // Not the usual `Should().ThrowAsync` lambda form: CA1416's platform-guard analyzer only
        // recognizes an `if (!OperatingSystem.IsWindows())` guard in the same method as the call it
        // protects, not through a separate lambda passed to FluentAssertions (see
        // LinuxUsbComPortDetectorTests' own comment on the same limitation).
        Exception? caught = null;
        try
        {
            await server.EnsureRunningAsync();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().BeOfType<InvalidOperationException>("starting should fail fast with the same reason IsAvailable already reports, not attempt to launch a nonexistent file");
    }

    [Fact]
    public async Task EnsureRunningAsync_StartsAProcess_AndDisposeAsyncStopsIt()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        await using var server = new BundledX11Server(NullLogger.Instance, CreateStandInExe(), FastPollInterval, FastPollAttempts);

        var endpoint = await server.EnsureRunningAsync();

        endpoint.Should().NotBeNull();
        server.ProcessId.Should().NotBeNull("a real process should have been spawned");
        using (var stillAlive = System.Diagnostics.Process.GetProcessById(server.ProcessId!.Value))
            stillAlive.HasExited.Should().BeFalse();

        var pid = server.ProcessId!.Value;
        await server.DisposeAsync();

        var stoppedCleanly = System.Diagnostics.Process.GetProcesses().All(p => p.Id != pid);
        stoppedCleanly.Should().BeTrue("DisposeAsync should have killed the process it started");
    }

    [Fact]
    public async Task EnsureRunningAsync_CalledAgainWhileAlreadyRunning_ReusesTheSameProcess()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        await using var server = new BundledX11Server(NullLogger.Instance, CreateStandInExe(), FastPollInterval, FastPollAttempts);

        var first = await server.EnsureRunningAsync();
        var firstPid = server.ProcessId;
        var second = await server.EnsureRunningAsync();

        second.Should().Be(first, "a live bundled server is shared infrastructure - a second caller should get the same endpoint, not trigger a second instance");
        server.ProcessId.Should().Be(firstPid);
    }
}
