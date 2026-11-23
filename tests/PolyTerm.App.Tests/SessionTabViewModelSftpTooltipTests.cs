using AwesomeAssertions;
using Moq;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="SessionTabViewModel.SftpUnavailableReason"/>/<see cref="SessionTabViewModel.SftpToolTip"/>:
/// the disabled SFTP controls explain why (from <see cref="IProtocolSession.FileTransferUnavailableReason"/>)
/// instead of just being greyed out (issue #227).
/// </summary>
public sealed class SessionTabViewModelSftpTooltipTests
{
    private sealed class FakeSession : IProtocolSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public ConnectionProfile Profile { get; } = new() { Name = "test", ProtocolId = "ssh" };
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<SessionErrorEventArgs>? ErrorOccurred { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ITerminalChannel? Terminal => null;
        public IFileTransferChannel? FileTransfer { get; init; }
        public string? FileTransferUnavailableReason { get; init; }
        public IFramebufferChannel? Framebuffer => null;
        public ITunnelManager? Tunnels => null;
        public INativeHostChannel? NativeHost => null;
        public IWebChannel? Web => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void WhenSftpIsAvailable_TheTooltipIsTheButtonLabel()
    {
        using var tab = new SessionTabViewModel(new FakeSession { FileTransfer = Mock.Of<IFileTransferChannel>() });

        tab.CanOpenSftp.Should().BeTrue();
        tab.SftpUnavailableReason.Should().BeNull();
        tab.SftpToolTip.Should().Be("Toggle SFTP Panel");
    }

    [Fact]
    public void WhenTheSessionGivesAReason_TheTooltipShowsIt()
    {
        using var tab = new SessionTabViewModel(new FakeSession { FileTransferUnavailableReason = "This host doesn't provide SFTP." });

        tab.CanOpenSftp.Should().BeFalse();
        tab.SftpUnavailableReason.Should().Be("This host doesn't provide SFTP.");
        tab.SftpToolTip.Should().Be("This host doesn't provide SFTP.");
    }

    [Fact]
    public void WhenTheProtocolNeverOffersFileTransfer_ThereIsNoReason()
    {
        using var tab = new SessionTabViewModel(new FakeSession());

        tab.SftpUnavailableReason.Should().BeNull();
        tab.SftpToolTip.Should().Be("Toggle SFTP Panel");
    }

    [Fact]
    public void AStaleReason_IsIgnored_OnceFileTransferIsAvailable()
    {
        using var tab = new SessionTabViewModel(new FakeSession
        {
            FileTransfer = Mock.Of<IFileTransferChannel>(),
            FileTransferUnavailableReason = "stale",
        });

        tab.SftpUnavailableReason.Should().BeNull();
    }
}
