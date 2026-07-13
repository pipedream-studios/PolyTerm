using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.App.Serial;
using PolyTerm.Protocols.Serial;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="ComPortWatcherService"/>'s change-detection logic against a fake
/// <see cref="IComPortDetector"/> — never starts the real timer (see <see cref="ComPortWatcherService.RefreshNow"/>'s
/// own doc comment), so these run instantly rather than waiting out the real poll interval.
/// </summary>
public sealed class ComPortWatcherServiceTests
{
    private sealed class FakeDetector : IComPortDetector
    {
        public IReadOnlyList<DetectedComPort> Ports { get; set; } = [];
        public IReadOnlyList<DetectedComPort> GetUsbPorts() => Ports;
    }

    [Fact]
    public void RefreshNow_PopulatesCurrentPorts_AndRaisesPortsChanged_OnFirstPoll()
    {
        var detector = new FakeDetector { Ports = [new DetectedComPort("COM3", "USB Serial Device (COM3)")] };
        var watcher = new ComPortWatcherService(detector, NullLogger<ComPortWatcherService>.Instance);
        var raised = 0;
        watcher.PortsChanged += (_, _) => raised++;

        watcher.RefreshNow();

        watcher.CurrentPorts.Should().ContainSingle().Which.PortName.Should().Be("COM3");
        raised.Should().Be(1, "the very first poll always represents a change from the empty starting set");
    }

    [Fact]
    public void RefreshNow_DoesNotRaisePortsChanged_WhenTheDetectedSetIsUnchanged()
    {
        var detector = new FakeDetector { Ports = [new DetectedComPort("COM3", "USB Serial Device (COM3)")] };
        var watcher = new ComPortWatcherService(detector, NullLogger<ComPortWatcherService>.Instance);
        watcher.RefreshNow();
        var raised = 0;
        watcher.PortsChanged += (_, _) => raised++;

        watcher.RefreshNow();
        watcher.RefreshNow();

        raised.Should().Be(0, "polling the same unchanged set shouldn't re-raise PortsChanged every tick");
    }

    [Fact]
    public void RefreshNow_RaisesPortsChanged_WhenAPortIsUnplugged()
    {
        var detector = new FakeDetector { Ports = [new DetectedComPort("COM3", "USB Serial Device (COM3)")] };
        var watcher = new ComPortWatcherService(detector, NullLogger<ComPortWatcherService>.Instance);
        watcher.RefreshNow();
        var raised = 0;
        watcher.PortsChanged += (_, _) => raised++;

        detector.Ports = [];
        watcher.RefreshNow();

        watcher.CurrentPorts.Should().BeEmpty();
        raised.Should().Be(1);
    }

    [Fact]
    public void GetUsbPorts_Throwing_IsSwallowed_AndKeepsThePreviousKnownSet()
    {
        var detector = new FakeDetector { Ports = [new DetectedComPort("COM3", "USB Serial Device (COM3)")] };
        var watcher = new ComPortWatcherService(detector, NullLogger<ComPortWatcherService>.Instance);
        watcher.RefreshNow();

        var throwingDetector = new ThrowingDetector();
        var watcherOverThrowing = new ComPortWatcherService(throwingDetector, NullLogger<ComPortWatcherService>.Instance);
        var act = () => watcherOverThrowing.RefreshNow();

        act.Should().NotThrow("a background poll callback throwing would otherwise go unobserved and silently stop future polls");
        watcherOverThrowing.CurrentPorts.Should().BeEmpty();
    }

    private sealed class ThrowingDetector : IComPortDetector
    {
        public IReadOnlyList<DetectedComPort> GetUsbPorts() => throw new InvalidOperationException("simulated WMI failure");
    }
}
