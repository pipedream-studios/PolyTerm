using AwesomeAssertions;
using PolyTerm.App.Macros;
using PolyTerm.App.ViewModels;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the actual add/edit/delete/run/broadcast logic behind the Macros window — entirely in
/// memory, never touching the user's real <c>%AppData%\PolyTerm\macros.json</c>. Possible because
/// <c>MacrosViewModel</c>'s constructor now takes the initial list and a save callback explicitly
/// (mirroring <c>TunnelsViewModel</c>'s injectable-editor-delegate pattern) instead of calling
/// <c>MacroStore.Load</c>/<c>Save</c> directly — see docs/testing.md for why this refactor was needed
/// (the old design had no injection point at all, so testing it safely wasn't possible without either
/// risking real user data or this change).
/// </summary>
public sealed class MacrosViewModelTests
{
    private static MacroDefinition MakeMacro(string name = "greet") => new() { Name = name, Body = "echo hi" };

    private static (MacrosViewModel Vm, List<IReadOnlyList<MacroDefinition>> SaveCalls) MakeViewModel(
        params MacroDefinition[] initial)
    {
        var saveCalls = new List<IReadOnlyList<MacroDefinition>>();
        var vm = new MacrosViewModel(initial, saved => saveCalls.Add(saved));
        return (vm, saveCalls);
    }

    [Fact]
    public async Task Add_AppendsResultAndPersists_WhenEditorConfirms()
    {
        var (vm, saveCalls) = MakeViewModel();
        var newMacro = MakeMacro();
        vm.EditMacroAsync = _ => Task.FromResult<MacroDefinition?>(newMacro);

        await vm.AddCommand.ExecuteAsync(null);

        vm.Macros.Should().ContainSingle().Which.Should().BeSameAs(newMacro);
        saveCalls.Should().ContainSingle().Which.Should().ContainSingle().Which.Should().BeSameAs(newMacro);
    }

    [Fact]
    public async Task Add_DoesNothing_WhenEditorCancelled()
    {
        var (vm, saveCalls) = MakeViewModel();
        vm.EditMacroAsync = _ => Task.FromResult<MacroDefinition?>(null);

        await vm.AddCommand.ExecuteAsync(null);

        vm.Macros.Should().BeEmpty();
        saveCalls.Should().BeEmpty("cancelling shouldn't persist anything");
    }

    [Fact]
    public async Task Add_IsSafeNoOp_WhenEditorDelegateNotSet()
    {
        var (vm, saveCalls) = MakeViewModel();

        await vm.AddCommand.ExecuteAsync(null);

        vm.Macros.Should().BeEmpty();
        saveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_ReplacesExistingEntry_AtTheSameIndex_AndPersists()
    {
        var first = MakeMacro("first");
        var second = MakeMacro("second");
        var (vm, saveCalls) = MakeViewModel(first, second);

        var replacement = MakeMacro("first-renamed");
        vm.EditMacroAsync = _ => Task.FromResult<MacroDefinition?>(replacement);

        await vm.EditAsync(first);

        vm.Macros.Should().HaveCount(2);
        vm.Macros[0].Should().BeSameAs(replacement);
        vm.Macros[1].Should().BeSameAs(second);
        saveCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Edit_LeavesListUnchanged_WhenEditorCancelled()
    {
        var macro = MakeMacro();
        var (vm, saveCalls) = MakeViewModel(macro);
        vm.EditMacroAsync = _ => Task.FromResult<MacroDefinition?>(null);

        await vm.EditAsync(macro);

        vm.Macros.Should().ContainSingle().Which.Should().BeSameAs(macro);
        saveCalls.Should().BeEmpty();
    }

    [Fact]
    public void Delete_RemovesTheGivenMacro_AndPersists()
    {
        var first = MakeMacro("first");
        var second = MakeMacro("second");
        var (vm, saveCalls) = MakeViewModel(first, second);

        vm.Delete(first);

        vm.Macros.Should().ContainSingle().Which.Should().BeSameAs(second);
        saveCalls.Should().ContainSingle().Which.Should().ContainSingle().Which.Should().BeSameAs(second);
    }

    [Fact]
    public async Task RunAsync_SetsError_WhenNoActiveSession()
    {
        var (vm, _) = MakeViewModel();
        vm.SendToActiveSessionAsync = _ => Task.FromResult(false);

        await vm.RunAsync(MakeMacro());

        vm.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunAsync_ClearsError_WhenSendSucceeds()
    {
        var (vm, _) = MakeViewModel();
        vm.SendToActiveSessionAsync = _ => Task.FromResult(true);
        vm.Error = "stale error from a previous attempt";

        await vm.RunAsync(MakeMacro());

        vm.Error.Should().BeNull();
    }

    [Fact]
    public async Task BroadcastMacroAsync_SetsError_WhenBroadcastNotArmed()
    {
        var (vm, _) = MakeViewModel();
        vm.BroadcastAsync = _ => Task.FromResult(false);

        await vm.BroadcastMacroAsync(MakeMacro());

        vm.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task BroadcastMacroAsync_ClearsError_WhenBroadcastSucceeds()
    {
        var (vm, _) = MakeViewModel();
        vm.BroadcastAsync = _ => Task.FromResult(true);
        vm.Error = "stale error from a previous attempt";

        await vm.BroadcastMacroAsync(MakeMacro());

        vm.Error.Should().BeNull();
    }
}
