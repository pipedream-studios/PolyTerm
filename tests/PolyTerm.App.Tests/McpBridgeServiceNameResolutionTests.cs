using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PolyTerm.App.McpBridge;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <c>McpBridgeService.ResolveProfileForOpenAsync</c> directly (bumped from private to
/// internal for exactly this purpose) — no named pipe, no real session, no App instance. The pipe
/// transport itself is a separate, currently-broken concern documented in docs/todos.md; this only
/// tests the name/id resolution logic that sits in front of it.
/// </summary>
public sealed class McpBridgeServiceNameResolutionTests
{
    private static ConnectionProfile MakeProfile(string name, bool isFolder = false) =>
        new() { Id = Guid.NewGuid(), Name = name, ProtocolId = isFolder ? "" : "ssh", IsFolder = isFolder };

    private static McpBridgeService MakeService(Mock<IProfileStore> profileStore) =>
        new(Mock.Of<ISessionManager>(), profileStore.Object, Mock.Of<IApprovalGate>(), NullLogger<McpBridgeService>.Instance);

    private static Mock<IProfileStore> StoreWith(params ConnectionProfile[] profiles)
    {
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profiles);
        // ResolveAsync is used to fetch the final (inheritance-applied) profile once a candidate id is chosen.
        store.Setup(s => s.ResolveAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => profiles.First(p => p.Id == id));
        return store;
    }

    private static JsonElement Params(string? name = null, string? profileId = null)
    {
        var dict = new Dictionary<string, object?>();
        if (name is not null) dict["name"] = name;
        if (profileId is not null) dict["profile_id"] = profileId;
        return JsonSerializer.SerializeToElement(dict);
    }

    [Fact]
    public async Task ResolvesByExactProfileId_IgnoringName()
    {
        var rump = MakeProfile("rump");
        var other = MakeProfile("other");
        var service = MakeService(StoreWith(rump, other));

        var result = await service.ResolveProfileForOpenAsync(
            Params(name: "completely different text", profileId: rump.Id.ToString()), TestContext.Current.CancellationToken);

        result.Should().BeSameAs(rump);
    }

    [Fact]
    public async Task ResolvesByExactCaseInsensitiveNameMatch()
    {
        var rump = MakeProfile("Rump");
        var service = MakeService(StoreWith(rump));

        var result = await service.ResolveProfileForOpenAsync(Params(name: "rump"), TestContext.Current.CancellationToken);

        result.Should().BeSameAs(rump);
    }

    [Fact]
    public async Task ResolvesByUnambiguousSubstringMatch()
    {
        var server = MakeProfile("production-web-server");
        var service = MakeService(StoreWith(server));

        var result = await service.ResolveProfileForOpenAsync(Params(name: "web"), TestContext.Current.CancellationToken);

        result.Should().BeSameAs(server);
    }

    /// <summary>Exact match must win outright even when it's also a substring of another candidate — otherwise searching "rump" when both "rump" and "rump2" exist would incorrectly report ambiguity.</summary>
    [Fact]
    public async Task ExactMatch_WinsOverAmbiguousSubstringCandidates()
    {
        var rump = MakeProfile("rump");
        var rump2 = MakeProfile("rump2");
        var service = MakeService(StoreWith(rump, rump2));

        var result = await service.ResolveProfileForOpenAsync(Params(name: "rump"), TestContext.Current.CancellationToken);

        result.Should().BeSameAs(rump);
    }

    [Fact]
    public async Task AmbiguousSubstringMatches_ThrowsListingCandidates()
    {
        var web1 = MakeProfile("web-server-1");
        var web2 = MakeProfile("web-server-2");
        var service = MakeService(StoreWith(web1, web2));

        var act = () => service.ResolveProfileForOpenAsync(Params(name: "web"), default);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("web-server-1").And.Contain("web-server-2").And.Contain("profile_id");
    }

    [Fact]
    public async Task NoMatch_ThrowsNotFound()
    {
        var service = MakeService(StoreWith(MakeProfile("rump")));

        var act = () => service.ResolveProfileForOpenAsync(Params(name: "definitely-does-not-exist"), default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No connection found*");
    }

    [Fact]
    public async Task NeitherNameNorProfileId_Throws()
    {
        var service = MakeService(StoreWith(MakeProfile("rump")));

        var act = () => service.ResolveProfileForOpenAsync(Params(), default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*name*profile_id*");
    }

    [Fact]
    public async Task FolderEntries_AreExcludedFromCandidates()
    {
        var folder = MakeProfile("web", isFolder: true);
        var service = MakeService(StoreWith(folder));

        var act = () => service.ResolveProfileForOpenAsync(Params(name: "web"), default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No connection found*");
    }
}
