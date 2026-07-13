using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Engine.Tests;

public sealed class SessionManagerTests
{
    /// <summary>
    /// SessionManager.ResolveJumpChainAsync is private, so this exercises it indirectly through the
    /// public OpenAsync contract — the thing that actually matters is that a cyclic jump-host chain
    /// is rejected with a clear error rather than an infinite loop / stack overflow, not the private
    /// method's own internals.
    /// </summary>
    [Fact]
    public async Task OpenAsync_ThrowsOnCyclicJumpHostChain()
    {
        var profileAId = Guid.NewGuid();
        var profileBId = Guid.NewGuid();

        var profileA = new ConnectionProfile { Id = profileAId, Name = "A", ProtocolId = "ssh" };
        profileA.Settings.SetJumpHostProfileId(profileBId);

        var profileB = new ConnectionProfile { Id = profileBId, Name = "B", ProtocolId = "ssh" };
        profileB.Settings.SetJumpHostProfileId(profileAId);

        var profileStore = new Mock<IProfileStore>();
        profileStore.Setup(s => s.ResolveAsync(profileAId, It.IsAny<CancellationToken>())).ReturnsAsync(profileA);
        profileStore.Setup(s => s.ResolveAsync(profileBId, It.IsAny<CancellationToken>())).ReturnsAsync(profileB);

        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.Find("ssh")).Returns(Mock.Of<IProtocolDescriptor>());

        var manager = new SessionManager(
            registry.Object,
            profileStore.Object,
            Array.Empty<ICredentialProvider>(),
            Mock.Of<IHostKeyPolicy>(),
            NullLogger<SessionManager>.Instance);

        var act = () => manager.OpenAsync(profileAId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cycle*");
    }

    /// <summary>Sanity check for the non-cyclic case, so the cycle test above is proven against real chain-walking behavior, not a mock that would reject anything.</summary>
    [Fact]
    public async Task OpenAsync_ResolvesNonCyclicJumpChain_WithoutThrowing()
    {
        var targetId = Guid.NewGuid();
        var bastionId = Guid.NewGuid();

        var target = new ConnectionProfile { Id = targetId, Name = "Target", ProtocolId = "ssh" };
        target.Settings.SetJumpHostProfileId(bastionId);

        var bastion = new ConnectionProfile { Id = bastionId, Name = "Bastion", ProtocolId = "ssh" };
        // No further jump host — chain terminates here.

        var profileStore = new Mock<IProfileStore>();
        profileStore.Setup(s => s.ResolveAsync(targetId, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        profileStore.Setup(s => s.ResolveAsync(bastionId, It.IsAny<CancellationToken>())).ReturnsAsync(bastion);

        var descriptor = new Mock<IProtocolDescriptor>();
        descriptor.Setup(d => d.CreateSession(It.IsAny<SessionContext>()))
            .Returns(() => throw new InvalidOperationException("stop-before-connect"));

        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.Find("ssh")).Returns(descriptor.Object);

        var manager = new SessionManager(
            registry.Object,
            profileStore.Object,
            Array.Empty<ICredentialProvider>(),
            Mock.Of<IHostKeyPolicy>(),
            NullLogger<SessionManager>.Instance);

        var act = () => manager.OpenAsync(targetId);

        // Chain resolution itself must succeed (no cycle error) — the test deliberately throws from
        // CreateSession afterward so this doesn't need a real, connectable protocol session.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("stop-before-connect");
    }
}
