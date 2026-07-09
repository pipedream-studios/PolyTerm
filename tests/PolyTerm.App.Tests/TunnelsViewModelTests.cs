using FluentAssertions;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the actual add/edit/delete list-manipulation logic behind the Tunnels editor — the part of
/// the M3 punch list (docs/todos.md) that matters, as distinct from "does clicking a button invoke a
/// RelayCommand" (which CommunityToolkit.Mvvm's source generator already handles reliably and doesn't
/// need re-testing). No Avalonia/window/dialog involved at all: <see cref="TunnelsViewModel"/> already
/// keeps the real dialog behind an injectable <c>EditTunnelAsync</c> delegate specifically to avoid
/// pulling a Window into the view model, which happens to make it directly unit-testable too.
/// </summary>
public sealed class TunnelsViewModelTests
{
    private static TunnelDefinition MakeTunnel(int bindPort = 8080) => new()
    {
        Kind = TunnelKind.Local, BindPort = bindPort, DestinationHost = "internal.example.com", DestinationPort = 80,
    };

    [Fact]
    public async Task Add_AppendsResult_WhenEditorConfirms()
    {
        var vm = new TunnelsViewModel([]);
        var newTunnel = MakeTunnel();
        vm.EditTunnelAsync = _ => Task.FromResult<TunnelDefinition?>(newTunnel);

        await vm.AddCommand.ExecuteAsync(null);

        vm.Tunnels.Should().ContainSingle().Which.Should().BeSameAs(newTunnel);
    }

    [Fact]
    public async Task Add_DoesNothing_WhenEditorCancelled()
    {
        var vm = new TunnelsViewModel([]);
        vm.EditTunnelAsync = _ => Task.FromResult<TunnelDefinition?>(null);

        await vm.AddCommand.ExecuteAsync(null);

        vm.Tunnels.Should().BeEmpty();
    }

    [Fact]
    public async Task Add_IsSafeNoOp_WhenEditorDelegateNotSet()
    {
        var vm = new TunnelsViewModel([]);
        // EditTunnelAsync deliberately left null — mirrors the window not having wired it up yet.

        await vm.AddCommand.ExecuteAsync(null);

        vm.Tunnels.Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_ReplacesExistingEntry_AtTheSameIndex_WhenEditorConfirms()
    {
        var first = MakeTunnel(8080);
        var second = MakeTunnel(9090);
        var vm = new TunnelsViewModel([first, second]);

        var replacement = MakeTunnel(8081);
        vm.EditTunnelAsync = _ => Task.FromResult<TunnelDefinition?>(replacement);

        await vm.EditAsync(first);

        vm.Tunnels.Should().HaveCount(2);
        vm.Tunnels[0].Should().BeSameAs(replacement);
        vm.Tunnels[1].Should().BeSameAs(second);
    }

    [Fact]
    public async Task Edit_LeavesListUnchanged_WhenEditorCancelled()
    {
        var first = MakeTunnel();
        var vm = new TunnelsViewModel([first]);
        vm.EditTunnelAsync = _ => Task.FromResult<TunnelDefinition?>(null);

        await vm.EditAsync(first);

        vm.Tunnels.Should().ContainSingle().Which.Should().BeSameAs(first);
    }

    [Fact]
    public void Delete_RemovesTheGivenTunnel()
    {
        var first = MakeTunnel(8080);
        var second = MakeTunnel(9090);
        var vm = new TunnelsViewModel([first, second]);

        vm.Delete(first);

        vm.Tunnels.Should().ContainSingle().Which.Should().BeSameAs(second);
    }

    [Fact]
    public void Result_SnapshotsCurrentTunnelList()
    {
        var tunnel = MakeTunnel();
        var vm = new TunnelsViewModel([tunnel]);

        var result = vm.Result;

        result.Should().ContainSingle().Which.Should().BeSameAs(tunnel);
        result.Should().NotBeSameAs(vm.Tunnels); // a snapshot, not a live view of the ObservableCollection
    }
}
