using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the Connection/Advanced/Organization tab selection on <see cref="NewConnectionViewModel"/>
/// (issue #68) — three plain bools rather than a real <c>TabControl</c>, so every tab's fields stay in
/// the visual tree (just <c>IsVisible</c>-gated) and remain reachable by the FlaUI suite regardless of
/// which tab is currently selected. See <see cref="NewConnectionViewModel.ConnectionTabSelected"/>'s own
/// doc comment for why a real TabControl would have broken that.
/// </summary>
public sealed class NewConnectionViewModelTabTests
{
    private sealed class FakeComPortWatcher : IComPortWatcher
    {
        public IReadOnlyList<PolyTerm.Protocols.Serial.DetectedComPort> CurrentPorts { get; } = [];
#pragma warning disable CS0067
        public event EventHandler? PortsChanged;
#pragma warning restore CS0067
    }

    private static NewConnectionViewModel MakeViewModel()
    {
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());

        var sessions = new Mock<ISessionManager>();
        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.All).Returns([]);
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);

        var tree = new ConnectionTreeViewModel(store.Object, sessions.Object, registry.Object, new FakeComPortWatcher());
        return new NewConnectionViewModel(store.Object, tree, registry.Object);
    }

    [AvaloniaFact]
    public void DefaultsToTheConnectionTab()
    {
        var vm = MakeViewModel();

        vm.ConnectionTabSelected.Should().BeTrue();
        vm.AdvancedTabSelected.Should().BeFalse();
        vm.OrganizationTabSelected.Should().BeFalse();
    }

    [AvaloniaFact]
    public void SelectingAdvanced_DeselectsTheOtherTwo()
    {
        var vm = MakeViewModel();

        vm.SelectAdvancedTabCommand.Execute(null);

        vm.ConnectionTabSelected.Should().BeFalse();
        vm.AdvancedTabSelected.Should().BeTrue();
        vm.OrganizationTabSelected.Should().BeFalse();
    }

    [AvaloniaFact]
    public void SelectingOrganization_DeselectsTheOtherTwo()
    {
        var vm = MakeViewModel();
        vm.SelectAdvancedTabCommand.Execute(null); // start from a non-default tab

        vm.SelectOrganizationTabCommand.Execute(null);

        vm.ConnectionTabSelected.Should().BeFalse();
        vm.AdvancedTabSelected.Should().BeFalse();
        vm.OrganizationTabSelected.Should().BeTrue();
    }

    [AvaloniaFact]
    public void SelectingConnection_AfterAnotherTab_RestoresTheDefault()
    {
        var vm = MakeViewModel();
        vm.SelectOrganizationTabCommand.Execute(null);

        vm.SelectConnectionTabCommand.Execute(null);

        vm.ConnectionTabSelected.Should().BeTrue();
        vm.AdvancedTabSelected.Should().BeFalse();
        vm.OrganizationTabSelected.Should().BeFalse();
    }
}
