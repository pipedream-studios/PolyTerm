using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine.Credentials;
using Xunit;

namespace PolyTerm.Engine.Tests.Credentials;

/// <summary>
/// Real, live coverage of <see cref="OsKeychainProvider"/>/<see cref="WindowsCredentialManager"/> —
/// this is the first test coverage either has ever had in this repo. Exercises the real Windows
/// Credential Manager (real <c>CredWrite</c>/<c>CredRead</c>/<c>CredDelete</c> calls), not a mock,
/// matching this session's own precedent for OS-integration tests (LinuxUsbComPortDetectorTests,
/// BundledX11ServerTests). Skips on non-Windows — the Linux (<c>secret-tool</c>) and macOS
/// (unimplemented) paths aren't exercisable in this dev environment.
/// </summary>
public sealed class OsKeychainProviderTests : IDisposable
{
    private const string SkipReason = "exercises the real Windows Credential Manager - only reachable on Windows in this dev environment.";

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
        if (!OperatingSystem.IsWindows()) return;
        foreach (var target in _writtenTargets)
            WindowsCredentialManager.Delete(target); // idempotent - safe even if a test already deleted it itself
    }

    [Fact]
    public async Task WriteAsync_ThenResolveAsync_RoundTripsTheRealSecret()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

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
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

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
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var act = async () => await OsKeychainProvider.DeleteAsync(UniqueTarget());

        await act.Should().NotThrowAsync("deleting a nonexistent Credential Manager entry should be idempotent, not an error");
    }

    [Fact]
    public async Task WriteAsync_TwiceToTheSameTarget_OverwritesRatherThanDuplicating()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var provider = new OsKeychainProvider(NullLogger<OsKeychainProvider>.Instance);
        var target = UniqueTarget();

        await provider.WriteAsync(target, "carol", "first-value");
        var reference = await provider.WriteAsync(target, "carol", "second-value");
        using var material = await provider.ResolveAsync(reference);

        System.Text.Encoding.UTF8.GetString(material.Secret!).Should().Be("second-value");
    }
}
