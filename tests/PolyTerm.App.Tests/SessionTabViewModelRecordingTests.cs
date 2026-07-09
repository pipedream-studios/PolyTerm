using System.Text;
using FluentAssertions;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="SessionTabViewModel"/>'s recording pause/resume wiring (the guard logic and
/// <see cref="SessionTabViewModel.IsRecordingActive"/> computed flag the UI's Pause/Resume menu items
/// and ribbon buttons key off of) — the recorders' own pause mechanism is covered separately in
/// <c>SessionRecorderRebindTests</c> against a fake channel; this file checks the view model glues it
/// together correctly (no-ops when not recording, resets on stop, etc).
/// </summary>
public sealed class SessionTabViewModelRecordingTests
{
    private sealed class FakeTerminalChannel : ITerminalChannel
    {
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public string TerminalType => "xterm-256color";
        public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default) => ValueTask.CompletedTask;
        public void Emit(string text) => OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));
    }

    private sealed class FakeProtocolSession : IProtocolSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public ConnectionProfile Profile { get; } = new() { Name = "test-session", ProtocolId = "ssh" };
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<SessionErrorEventArgs>? ErrorOccurred { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ITerminalChannel? Terminal { get; } = new FakeTerminalChannel();
        public IFileTransferChannel? FileTransfer => null;
        public IFramebufferChannel? Framebuffer => null;
        public ITunnelManager? Tunnels => null;
        public INativeHostChannel? NativeHost => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SessionTabViewModel MakeTab() => new(new FakeProtocolSession());

    private static string TempPath(string ext) => Path.Combine(Path.GetTempPath(), $"polyterm-test-{Guid.NewGuid():N}.{ext}");

    [Fact]
    public void StartRecording_SetsIsRecordingAndIsRecordingActive_ButNotPaused()
    {
        using var tab = MakeTab();
        var path = TempPath("log");
        try
        {
            tab.StartRecording(path, asciicast: false);

            tab.IsRecording.Should().BeTrue();
            tab.IsRecordingPaused.Should().BeFalse();
            tab.IsRecordingActive.Should().BeTrue();
        }
        finally { tab.StopRecording(); File.Delete(path); }
    }

    [Fact]
    public void PauseRecording_SetsIsRecordingPaused_AndClearsIsRecordingActive()
    {
        using var tab = MakeTab();
        var path = TempPath("log");
        try
        {
            tab.StartRecording(path, asciicast: false);

            tab.PauseRecording();

            tab.IsRecording.Should().BeTrue("pausing doesn't stop the recording, just the writes");
            tab.IsRecordingPaused.Should().BeTrue();
            tab.IsRecordingActive.Should().BeFalse();
        }
        finally { tab.StopRecording(); File.Delete(path); }
    }

    [Fact]
    public void ResumeRecording_ClearsIsRecordingPaused_AndRestoresIsRecordingActive()
    {
        using var tab = MakeTab();
        var path = TempPath("log");
        try
        {
            tab.StartRecording(path, asciicast: false);
            tab.PauseRecording();

            tab.ResumeRecording();

            tab.IsRecordingPaused.Should().BeFalse();
            tab.IsRecordingActive.Should().BeTrue();
        }
        finally { tab.StopRecording(); File.Delete(path); }
    }

    [Fact]
    public void PauseRecording_IsANoOp_WhenNotRecording()
    {
        using var tab = MakeTab();

        tab.PauseRecording();

        tab.IsRecordingPaused.Should().BeFalse();
    }

    [Fact]
    public void ResumeRecording_IsANoOp_WhenNotPaused()
    {
        using var tab = MakeTab();
        var path = TempPath("log");
        try
        {
            tab.StartRecording(path, asciicast: false);

            tab.ResumeRecording(); // never paused — should be harmless

            tab.IsRecordingPaused.Should().BeFalse();
            tab.IsRecordingActive.Should().BeTrue();
        }
        finally { tab.StopRecording(); File.Delete(path); }
    }

    [Fact]
    public void StopRecording_ResetsRecordingAndPausedFlags()
    {
        using var tab = MakeTab();
        var path = TempPath("log");
        tab.StartRecording(path, asciicast: false);
        tab.PauseRecording();

        tab.StopRecording();

        tab.IsRecording.Should().BeFalse();
        tab.IsRecordingPaused.Should().BeFalse();
        tab.IsRecordingActive.Should().BeFalse();
        File.Delete(path);
    }
}
