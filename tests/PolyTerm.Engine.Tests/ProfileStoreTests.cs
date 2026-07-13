using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using PolyTerm.Engine.Storage;
using Xunit;

namespace PolyTerm.Engine.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ProfileStore _store;

    public ProfileStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"polyterm-test-{Guid.NewGuid()}.db");
        _store  = new ProfileStore(_dbPath, NullLogger<ProfileStore>.Instance);
    }

    [Fact]
    public async Task SaveAndRetrieve_RoundTrips()
    {
        var profile = new ConnectionProfile
        {
            Name       = "Test SSH",
            ProtocolId = "ssh",
            Host       = "192.168.1.1",
            Port       = 22,
        };

        await _store.SaveAsync(profile);
        var retrieved = await _store.GetByIdAsync(profile.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Test SSH");
        retrieved.Host.Should().Be("192.168.1.1");
        retrieved.Port.Should().Be(22);
    }

    [Fact]
    public async Task Delete_RemovesProfile()
    {
        var profile = new ConnectionProfile { Name = "To Delete", ProtocolId = "ssh" };
        await _store.SaveAsync(profile);
        await _store.DeleteAsync(profile.Id);

        var retrieved = await _store.GetByIdAsync(profile.Id);
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task GetChildren_ReturnsFolderContents()
    {
        var folder = new ConnectionProfile { Name = "Folder", ProtocolId = "", IsFolder = true };
        await _store.SaveAsync(folder);

        var child = new ConnectionProfile { Name = "Child SSH", ProtocolId = "ssh", ParentId = folder.Id };
        await _store.SaveAsync(child);

        var children = await _store.GetChildrenAsync(folder.Id);
        children.Should().ContainSingle(p => p.Id == child.Id);
    }

    [Fact]
    public async Task Update_ChangesName()
    {
        var profile = new ConnectionProfile { Name = "Original", ProtocolId = "ssh" };
        await _store.SaveAsync(profile);

        profile.Name = "Updated";
        await _store.SaveAsync(profile);

        var retrieved = await _store.GetByIdAsync(profile.Id);
        retrieved!.Name.Should().Be("Updated");
    }

    [Fact]
    public async Task ResolveAsync_InheritsHostFromParent_WhenOwnHostIsNull()
    {
        var parent = new ConnectionProfile { Name = "Parent", ProtocolId = "", IsFolder = true, Host = "parent-host" };
        await _store.SaveAsync(parent);

        var child = new ConnectionProfile
        {
            Name = "Child", ProtocolId = "ssh", ParentId = parent.Id,
            Host = null, Inherit = InheritanceMask.Host,
        };
        await _store.SaveAsync(child);

        var resolved = await _store.ResolveAsync(child.Id);

        resolved.Host.Should().Be("parent-host");
    }

    [Fact]
    public async Task ResolveAsync_DoesNotInherit_WhenInheritFlagNotSet()
    {
        var parent = new ConnectionProfile { Name = "Parent", ProtocolId = "", IsFolder = true, Host = "parent-host" };
        await _store.SaveAsync(parent);

        var child = new ConnectionProfile
        {
            Name = "Child", ProtocolId = "ssh", ParentId = parent.Id,
            Host = null, Inherit = InheritanceMask.None,
        };
        await _store.SaveAsync(child);

        var resolved = await _store.ResolveAsync(child.Id);

        resolved.Host.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_DoesNotOverwrite_OwnNonNullHost()
    {
        var parent = new ConnectionProfile { Name = "Parent", ProtocolId = "", IsFolder = true, Host = "parent-host" };
        await _store.SaveAsync(parent);

        var child = new ConnectionProfile
        {
            Name = "Child", ProtocolId = "ssh", ParentId = parent.Id,
            Host = "own-host", Inherit = InheritanceMask.Host,
        };
        await _store.SaveAsync(child);

        var resolved = await _store.ResolveAsync(child.Id);

        resolved.Host.Should().Be("own-host");
    }

    /// <summary>
    /// Locks in a genuinely counter-intuitive, verified-by-reading-the-code behavior: when multiple
    /// ancestors up the chain have a value, the OUTERMOST one wins, not the nearest parent — ancestors
    /// are applied outermost-first, and each field is only filled while still null, so the nearest
    /// parent's value never gets a chance once a further-out ancestor has already filled the slot.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_OutermostAncestor_WinsOverNearerParent()
    {
        var grandparent = new ConnectionProfile { Name = "Grandparent", ProtocolId = "", IsFolder = true, Host = "far-host" };
        await _store.SaveAsync(grandparent);

        var parent = new ConnectionProfile
        {
            Name = "Parent", ProtocolId = "", IsFolder = true, ParentId = grandparent.Id, Host = "near-host",
        };
        await _store.SaveAsync(parent);

        var child = new ConnectionProfile
        {
            Name = "Child", ProtocolId = "ssh", ParentId = parent.Id,
            Host = null, Inherit = InheritanceMask.Host,
        };
        await _store.SaveAsync(child);

        var resolved = await _store.ResolveAsync(child.Id);

        resolved.Host.Should().Be("far-host", "the outermost ancestor with a value wins, not the nearest parent");
    }

    [Fact]
    public async Task ResolveAsync_InheritsPortAndCredential_WhenFlagsSet()
    {
        var credential = new CredentialReference("os-keychain", "some-item-ref");
        var parent = new ConnectionProfile
        {
            Name = "Parent", ProtocolId = "", IsFolder = true, Port = 2222, Credential = credential,
        };
        await _store.SaveAsync(parent);

        var child = new ConnectionProfile
        {
            Name = "Child", ProtocolId = "ssh", ParentId = parent.Id,
            Port = null, Credential = null, Inherit = InheritanceMask.Port | InheritanceMask.Credential,
        };
        await _store.SaveAsync(child);

        var resolved = await _store.ResolveAsync(child.Id);

        resolved.Port.Should().Be(2222);
        resolved.Credential.Should().Be(credential);
    }

    /// <summary>
    /// Tripwire, not a mistake: <see cref="InheritanceMask.Settings"/> is declared but
    /// <c>ProfileStore.ApplyInheritanceAsync</c> never actually acts on it (confirmed by reading the
    /// code — only Host/Port/Credential are handled). If this test starts failing, Settings
    /// inheritance has been implemented for real — update this test and the corresponding notes in
    /// docs/todos.md and docs/polyterm-spec.md rather than treating the failure as a regression.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_DoesNotInheritSettings_KnownGap()
    {
        var parent = new ConnectionProfile
        {
            Name = "Parent", ProtocolId = "", IsFolder = true,
            Settings = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["mcpPolicy"] = System.Text.Json.JsonSerializer.SerializeToElement("Allow"),
            },
        };
        await _store.SaveAsync(parent);

        var child = new ConnectionProfile
        {
            Name = "Child", ProtocolId = "ssh", ParentId = parent.Id,
            Inherit = InheritanceMask.Settings,
        };
        await _store.SaveAsync(child);

        var resolved = await _store.ResolveAsync(child.Id);

        resolved.Settings.Should().NotContainKey("mcpPolicy");
    }

    public void Dispose()
    {
        (_store as IAsyncDisposable)?.DisposeAsync().AsTask().Wait();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
