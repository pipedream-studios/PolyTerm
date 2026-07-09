using FluentAssertions;
using PolyTerm.App.CommandPalette;
using PolyTerm.App.ViewModels;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>Search/selection/confirm state machine behind the M4 command palette (Ctrl+Shift+P) — pure ViewModel logic, no Avalonia headless needed.</summary>
public sealed class CommandPaletteViewModelTests
{
    private static PaletteCommand Cmd(string title, Action? execute = null) =>
        new(title, "Session", null, execute ?? (() => { }));

    [Fact]
    public void Constructor_ShowsAllCommands_WithTheFirstOneSelected()
    {
        var a = Cmd("New Connection…");
        var b = Cmd("Quick Connect…");
        var vm = new CommandPaletteViewModel([a, b]);

        vm.FilteredCommands.Should().Equal(a, b);
        vm.SelectedCommand.Should().Be(a);
    }

    [Fact]
    public void TypingASearch_NarrowsFilteredCommands_AndReselectsTheTopMatch()
    {
        var connect = Cmd("Quick Connect…");
        var shell = Cmd("New Local Shell");
        var vm = new CommandPaletteViewModel([connect, shell]);

        vm.SearchText = "shell";

        vm.FilteredCommands.Should().Equal(shell);
        vm.SelectedCommand.Should().Be(shell);
    }

    [Fact]
    public void ClearingTheSearch_RestoresTheFullList()
    {
        var connect = Cmd("Quick Connect…");
        var shell = Cmd("New Local Shell");
        var vm = new CommandPaletteViewModel([connect, shell]) { SearchText = "shell" };

        vm.SearchText = "";

        vm.FilteredCommands.Should().Equal(connect, shell);
    }

    [Fact]
    public void MoveSelection_StepsForwardAndWrapsAtTheEnd()
    {
        var a = Cmd("A"); var b = Cmd("B"); var c = Cmd("C");
        var vm = new CommandPaletteViewModel([a, b, c]);

        vm.MoveSelection(+1);
        vm.SelectedCommand.Should().Be(b);
        vm.MoveSelection(+1);
        vm.SelectedCommand.Should().Be(c);
        vm.MoveSelection(+1);
        vm.SelectedCommand.Should().Be(a, "selection wraps back to the start past the last item");
    }

    [Fact]
    public void MoveSelection_StepsBackwardAndWrapsAtTheStart()
    {
        var a = Cmd("A"); var b = Cmd("B");
        var vm = new CommandPaletteViewModel([a, b]);

        vm.MoveSelection(-1);

        vm.SelectedCommand.Should().Be(b, "selection wraps to the last item when moving back from the first");
    }

    [Fact]
    public void MoveSelection_OnEmptyFilteredList_DoesNothing()
    {
        var vm = new CommandPaletteViewModel([Cmd("Only")]) { SearchText = "no-match-xyz" };

        vm.MoveSelection(+1);

        vm.SelectedCommand.Should().BeNull();
    }

    [Fact]
    public void Confirm_SetsConfirmedTrue_WhenACommandIsSelected()
    {
        var vm = new CommandPaletteViewModel([Cmd("Only")]);

        vm.Confirm();

        vm.Confirmed.Should().BeTrue();
    }

    [Fact]
    public void Confirm_DoesNothing_WhenNoCommandIsSelected()
    {
        var vm = new CommandPaletteViewModel([Cmd("Only")]) { SearchText = "no-match-xyz" };

        vm.Confirm();

        vm.Confirmed.Should().BeFalse();
    }

    [Fact]
    public void ConfirmedCommands_Execute_RunsTheUnderlyingAction()
    {
        var ran = false;
        var vm = new CommandPaletteViewModel([Cmd("Do Thing", () => ran = true)]);

        vm.Confirm();
        vm.SelectedCommand!.Execute();

        ran.Should().BeTrue();
    }
}
