using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine.Credentials;
using Xunit;

namespace PolyTerm.Engine.Tests.Credentials;

/// <summary>
/// Real, live coverage of <see cref="OsKeychainProvider"/> against the real OS secret store, not a
/// mock: Windows Credential Manager (<c>CredWrite</c>/<c>CredRead</c>/<c>CredDelete</c>) and the macOS
/// login keychain (<see cref="MacKeychain"/>'s SecItem calls), matching this repo's precedent for
/// OS-integration tests (LinuxUsbComPortDetectorTests, BundledX11ServerTests). Skips on Linux (the
/// <c>secret-tool</c> path needs a running Secret Service), and on macOS under CI: a self-hosted
/// runner's service session may have no unlocked login keychain, where the calls would fail or wait
/// on an access prompt nobody can answer.
/// </summary>
public sealed class OsKeychainProviderTests : IDisposable
{
    private const string SkipReason = "exercises the real OS keychain - only run on Windows, and on macOS outside CI.";

    /// <summary>Whether the real keychain is reachable here - see the class summary for why macOS CI and Linux are excluded.</summary>
    private static bool CanRun =>
        OperatingSystem.IsWindows()
        || (OperatingSystem.IsMacOS() && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));

    private readonly List<string> _writtenTargets = [];

    /// <summary>A fresh, unique target per test so parallel runs (and repeated local runs) never collide on the same real Credential Manager entry.</summary>
    private string UniqueTarget()
    {
        var target = $"PolyTerm-Test/{Guid.NewGuid():N}";
        _writtenTargets.Add(target);
        return target;
    }

    public void Dispose()
    {
        if (!CanRun) return;
        foreach (var target in _writtenTargets)
            OsKeychainProvider.DeleteAsync(target).GetAwaiter().GetResult(); // idempotent - safe even if a test already deleted it itself
    }

    [Fact]
    public async Task WriteAsync_ThenResolveAsync_RoundTripsTheRealSecret()
    {
        if (!CanRun) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var provider = new OsKeychainProvider(NullLogger<OsKeychainProvider>.Instance);
        var target = UniqueTarget();

        var reference = await provider.WriteAsync(target, "alice", "s3cr3t-value");
        using var material = await provider.ResolveAsync(reference);

        material.Username.Should().Be("alice");
        System.Text.Encoding.UTF8.GetString(material.Secret!).Should().Be("s3cr3t-value");
    }

    [Fact]
    public async Task DeleteAsync_ThenResolveAsync_ReturnsNoSecret()
    {
        if (!CanRun) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var provider = new OsKeychainProvider(NullLogger<OsKeychainProvider>.Instance);
        var target = UniqueTarget();
        var reference = await provider.WriteAsync(target, "bob", "another-secret");

        await OsKeychainProvider.DeleteAsync(target);

        using var material = await provider.ResolveAsync(reference);
        material.Secret.Should().BeNull("the entry was just deleted - nothing should resolve");
    }

    [Fact]
    public async Task DeleteAsync_OnATargetThatWasNeverWritten_IsASafeNoOp()
    {
        if (!CanRun) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var act = async () => await OsKeychainProvider.DeleteAsync(UniqueTarget());

        await act.Should().NotThrowAsync("deleting a nonexistent keychain entry should be idempotent, not an error");
    }

    [Fact]
    public async Task WriteAsync_TwiceToTheSameTarget_OverwritesRatherThanDuplicating()
    {
        if (!CanRun) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var provider = new OsKeychainProvider(NullLogger<OsKeychainProvider>.Instance);
        var target = UniqueTarget();

        await provider.WriteAsync(target, "carol", "first-value");
        var reference = await provider.WriteAsync(target, "carol", "second-value");
        using var material = await provider.ResolveAsync(reference);

        System.Text.Encoding.UTF8.GetString(material.Secret!).Should().Be("second-value");
    }
}
