using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>Covers <see cref="MainViewModel.ConnectionTreeVisible"/> (issue #74's View &gt; Connection Tree toggle).</summary>
public sealed class MainViewModelTests
{
    private sealed class FakeComPortWatcher : IComPortWatcher
    {
        public IReadOnlyList<PolyTerm.Protocols.Serial.DetectedComPort> CurrentPorts { get; } = [];
#pragma warning disable CS0067
        public event EventHandler? PortsChanged;
#pragma warning restore CS0067
    }

    private static MainViewModel MakeViewModel()
    {
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        var sessions = new Mock<ISessionManager>();
        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);

        var tree = new ConnectionTreeViewModel(store.Object, sessions.Object, registry.Object, new FakeComPortWatcher());
        return new MainViewModel(sessions.Object, tree);
    }

    [Fact]
    public void ConnectionTreeVisible_DefaultsToTrue()
    {
        var vm = MakeViewModel();

        vm.ConnectionTreeVisible.Should().BeTrue("the sidebar is shown by default — the toggle only hides it on request");
    }

    [Fact]
    public void ConnectionTreeVisible_CanBeToggledOffAndBackOn()
    {
        var vm = MakeViewModel();

        vm.ConnectionTreeVisible = false;
        vm.ConnectionTreeVisible.Should().BeFalse();

        vm.ConnectionTreeVisible = true;
        vm.ConnectionTreeVisible.Should().BeTrue();
    }
}
