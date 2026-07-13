using System.Text;
using AwesomeAssertions;
using PolyTerm.App.Recording;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the reconnect-survival fix for both recorders: after an SSH auto-reconnect swaps in a
/// fresh <see cref="ITerminalChannel"/>, <see cref="ISessionRecorder.Rebind"/> should make the
/// recorder tail the new channel while continuing to append to the same file — not restart it
/// (which would truncate everything captured before the reconnect) and not keep listening to the
/// dead old channel.
/// </summary>
public sealed class SessionRecorderRebindTests
{
    private sealed class FakeTerminalChannel : ITerminalChannel
    {
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public string TerminalType => "xterm-256color";
        public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default) => ValueTask.CompletedTask;
        public void Emit(string text) => OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));
        public bool HasSubscribers => OutputReceived is not null;
    }

    [Fact]
    public void RawSessionRecorder_Rebind_StopsTailingOldChannel_AndTailsNewOneIntoSameFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyterm-test-{Guid.NewGuid():N}.log");
        var oldChannel = new FakeTerminalChannel();
        var newChannel = new FakeTerminalChannel();
        try
        {
            using var recorder = new RawSessionRecorder(path, oldChannel);
            oldChannel.Emit("before-reconnect");

            recorder.Rebind(newChannel);
            oldChannel.HasSubscribers.Should().BeFalse("the dead channel should no longer be tailed after rebind");

            oldChannel.Emit("should-not-appear");
            newChannel.Emit("after-reconnect");

            recorder.Dispose();
            var content = File.ReadAllText(path);
            content.Should().Contain("before-reconnect");
            content.Should().Contain("after-reconnect");
            content.Should().NotContain("should-not-appear");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AsciicastRecorder_Rebind_StopsTailingOldChannel_AndTailsNewOneIntoSameFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyterm-test-{Guid.NewGuid():N}.cast");
        var oldChannel = new FakeTerminalChannel();
        var newChannel = new FakeTerminalChannel();
        try
        {
            using var recorder = new AsciicastRecorder(path, oldChannel, cols: 80, rows: 24, title: "test session");
            oldChannel.Emit("before-reconnect");

            recorder.Rebind(newChannel);
            oldChannel.HasSubscribers.Should().BeFalse("the dead channel should no longer be tailed after rebind");

            oldChannel.Emit("should-not-appear");
            newChannel.Emit("after-reconnect");

            recorder.Dispose();
            var lines = File.ReadAllLines(path);
            lines.Should().HaveCountGreaterThanOrEqualTo(3, "header + 2 output events");
            var events = string.Join('\n', lines.Skip(1));
            events.Should().Contain("before-reconnect");
            events.Should().Contain("after-reconnect");
            events.Should().NotContain("should-not-appear");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RawSessionRecorder_Pause_StopsWritingOutput_UntilResume()
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyterm-test-{Guid.NewGuid():N}.log");
        var channel = new FakeTerminalChannel();
        try
        {
            using var recorder = new RawSessionRecorder(path, channel);
            channel.Emit("before-pause");

            recorder.IsPaused.Should().BeFalse();
            recorder.Pause();
            recorder.IsPaused.Should().BeTrue();

            channel.Emit("while-paused");

            recorder.Resume();
            recorder.IsPaused.Should().BeFalse();
            channel.Emit("after-resume");

            recorder.Dispose();
            var content = File.ReadAllText(path);
            content.Should().Contain("before-pause");
            content.Should().Contain("after-resume");
            content.Should().NotContain("while-paused");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AsciicastRecorder_Pause_StopsWritingOutput_UntilResume()
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyterm-test-{Guid.NewGuid():N}.cast");
        var channel = new FakeTerminalChannel();
        try
        {
            using var recorder = new AsciicastRecorder(path, channel, cols: 80, rows: 24, title: "test session");
            channel.Emit("before-pause");

            recorder.Pause();
            channel.Emit("while-paused");
            recorder.Resume();
            channel.Emit("after-resume");

            recorder.Dispose();
            var events = string.Join('\n', File.ReadAllLines(path).Skip(1));
            events.Should().Contain("before-pause");
            events.Should().Contain("after-resume");
            events.Should().NotContain("while-paused");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
