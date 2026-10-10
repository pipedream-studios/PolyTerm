using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PolyTerm.Engine;
using PolyTerm.Engine.HostKeyPolicy;
using PolyTerm.Engine.Storage;
using Xunit;

namespace PolyTerm.Engine.Tests;

public sealed class TofuHostKeyPolicyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"polyterm-hostkeys-{Guid.NewGuid():N}.db");
    private readonly HostKeyStore _store;

    public TofuHostKeyPolicyTests() => _store = new HostKeyStore(_dbPath, NullLogger<HostKeyStore>.Instance);

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*"))
            try { File.Delete(f); } catch (IOException) { }
    }

    private static HostKeyInfo Key(string fingerprint) => new()
    {
        Host = "example.test", Port = 22, Algorithm = "ssh-ed25519",
        FingerprintSha256 = fingerprint, KeyBytes = [1, 2, 3],
    };

    private TofuHostKeyPolicy PolicyWith(HostKeyChangeChoice? choice, out Mock<IHostKeyChangePrompt>? prompt)
    {
        prompt = null;
        if (choice is null)
            return new TofuHostKeyPolicy(_store, NullLogger<TofuHostKeyPolicy>.Instance);

        prompt = new Mock<IHostKeyChangePrompt>();
        prompt.Setup(p => p.AskAsync(It.IsAny<HostKeyChangeInfo>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(choice.Value);
        return new TofuHostKeyPolicy(_store, NullLogger<TofuHostKeyPolicy>.Instance, prompt.Object);
    }

    private async Task SeedAsync() => await _store.TrustAsync(Key("OLD"));

    [Fact]
    public async Task ChangedKey_WithoutPrompt_ReturnsPromptAndKeepsStoredKey()
    {
        await SeedAsync();
        var decision = await PolicyWith(null, out _).EvaluateAsync(Key("NEW"));

        decision.Should().Be(HostKeyDecision.Prompt);
        (await _store.FindAsync("example.test", 22, "ssh-ed25519"))!.FingerprintSha256.Should().Be("OLD");
    }

    [Fact]
    public async Task ChangedKey_PromptShowsKnownAndNewKey()
    {
        await SeedAsync();
        var policy = PolicyWith(HostKeyChangeChoice.Disconnect, out var prompt);
        await policy.EvaluateAsync(Key("NEW"));

        prompt!.Verify(p => p.AskAsync(
            It.Is<HostKeyChangeInfo>(i => i.KnownFingerprintSha256 == "OLD" && i.NewFingerprintSha256 == "NEW" && i.Host == "example.test"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangedKey_Disconnect_RejectsAndKeepsStoredKey()
    {
        await SeedAsync();
        var decision = await PolicyWith(HostKeyChangeChoice.Disconnect, out _).EvaluateAsync(Key("NEW"));

        decision.Should().Be(HostKeyDecision.Reject);
        (await _store.FindAsync("example.test", 22, "ssh-ed25519"))!.FingerprintSha256.Should().Be("OLD");
    }

    [Fact]
    public async Task ChangedKey_TrustOnce_TrustsButKeepsStoredKey()
    {
        await SeedAsync();
        var decision = await PolicyWith(HostKeyChangeChoice.TrustOnce, out _).EvaluateAsync(Key("NEW"));

        decision.Should().Be(HostKeyDecision.Trust);
        (await _store.FindAsync("example.test", 22, "ssh-ed25519"))!.FingerprintSha256.Should().Be("OLD");
    }

    [Fact]
    public async Task ChangedKey_TrustPermanently_TrustsAndReplacesStoredKey()
    {
        await SeedAsync();
        var decision = await PolicyWith(HostKeyChangeChoice.TrustPermanently, out _).EvaluateAsync(Key("NEW"));

        decision.Should().Be(HostKeyDecision.Trust);
        (await _store.FindAsync("example.test", 22, "ssh-ed25519"))!.FingerprintSha256.Should().Be("NEW");
    }

    [Fact]
    public async Task UnchangedKey_NeverPrompts()
    {
        await SeedAsync();
        var policy = PolicyWith(HostKeyChangeChoice.Disconnect, out var prompt);
        var decision = await policy.EvaluateAsync(Key("OLD"));

        decision.Should().Be(HostKeyDecision.Trust);
        prompt!.Verify(p => p.AskAsync(It.IsAny<HostKeyChangeInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
