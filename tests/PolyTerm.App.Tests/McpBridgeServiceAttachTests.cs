using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PolyTerm.App.McpBridge;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the <c>attach_start</c>/<c>attach_read</c>/<c>attach_send_input</c>/<c>attach_stop</c> pipe
/// methods added for issue #114/#116 ("PolyTerm.Cli attach over IPC") — the mechanism <c>polyterm
/// attach</c> uses to tail and drive a session that's already open (or detached but still live) in a
/// running <c>PolyTerm.App</c>. Talks to a real <see cref="McpBridgeService"/> over a real named pipe
/// (private per-test pipe name), the same way <see cref="McpBridgeServicePipeTests"/> does, since the
/// whole point is exercising the real JSON-RPC wire protocol <c>AttachPipeClient</c>/<c>AttachCommand</c>
/// speak against, not just the C# methods in isolation.
/// </summary>
[Collection("AppSettings")]
public sealed class McpBridgeServiceAttachTests
{
    private sealed class FakeTerminalChannel : ITerminalChannel
    {
        private Action<ReadOnlyMemory<byte>>? _sink;
        public List<string> SentInput { get; } = [];

#pragma warning disable CS0067
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
#pragma warning restore CS0067
        public string TerminalType => "xterm-256color";

        public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            SentInput.Add(Encoding.UTF8.GetString(data.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default) => ValueTask.CompletedTask;

        public void RegisterOutputSink(Action<ReadOnlyMemory<byte>> sink) => _sink = sink;

        public void EmitLine(string line) => _sink?.Invoke(Encoding.UTF8.GetBytes(line + "\n"));
    }

    private sealed class FakeProtocolSession : IProtocolSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public required ConnectionProfile Profile { get; init; }
        public SessionState State { get; set; } = SessionState.Connected;
        public required FakeTerminalChannel FakeTerminal { get; init; }

#pragma warning disable CS0067
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ITerminalChannel? Terminal => FakeTerminal;
        public IFileTransferChannel? FileTransfer => null;
        public IFramebufferChannel? Framebuffer => null;
        public ITunnelManager? Tunnels => null;
        public INativeHostChannel? NativeHost => null;
        public IWebChannel? Web => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Harness(McpBridgeService Service, FakeProtocolSession Session, PipeCaller Call);

    /// <summary>Small round-trip helper over a real <see cref="NamedPipeClientStream"/> — same framing <see cref="McpBridgeServicePipeTests"/> hand-rolls, factored out here since this file needs several round trips per test.</summary>
    private sealed class PipeCaller(NamedPipeClientStream pipe) : IDisposable
    {
        private readonly StreamReader _reader = new(pipe, new UTF8Encoding(false), leaveOpen: true);
        private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        private int _id;

        public async Task<JsonElement> CallAsync(string method, object? args, CancellationToken ct)
        {
            var id = ++_id;
            await _writer.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = args ?? new { } }).AsMemory(), ct).ConfigureAwait(false);
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new EndOfStreamException();
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException(err.GetString());
            return root.GetProperty("result").Clone();
        }

        public void Dispose() => pipe.Dispose();
    }

    private static async Task<(Harness Harness, IDisposable Cleanup)> StartAsync(CancellationToken ct)
    {
        var pipeName = $"polyterm-test-{Guid.NewGuid():N}";
        var sessions = new Mock<ISessionManager>();
        var profiles = new Mock<IProfileStore>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());

        var service = new McpBridgeService(sessions.Object, profiles.Object, Mock.Of<IApprovalGate>(), NullLogger<McpBridgeService>.Instance, pipeName);
        service.Start();
        await Task.Delay(200, ct).ConfigureAwait(false);

        var terminal = new FakeTerminalChannel();
        var session = new FakeProtocolSession { Profile = new ConnectionProfile { Name = "attach-test", ProtocolId = "ssh" }, FakeTerminal = terminal };
        sessions.Raise(s => s.SessionOpened += null, sessions.Object, new SessionEventArgs { Session = session });

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

        var caller = new PipeCaller(pipe);
        return (new Harness(service, session, caller), new CombinedDisposable(pipe, service));
    }

    private sealed class CombinedDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var i in items) i.Dispose(); }
    }

    [Fact]
    public async Task AttachStart_ReturnsBacklogAndTotalLines()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(ct);
        using var _ = cleanup;

        h.Session.FakeTerminal.EmitLine("hello");
        h.Session.FakeTerminal.EmitLine("world");
        await Task.Delay(50, ct); // let the sink append synchronously-invoked lines settle

        var result = await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);

        result.GetProperty("text").GetString().Should().Be("hello\nworld");
        result.GetProperty("total_lines").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task AttachRead_OnlyReturnsLinesCommittedSinceTheGivenCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(ct);
        using var _ = cleanup;

        h.Session.FakeTerminal.EmitLine("line1");
        await Task.Delay(50, ct);
        var started = await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);
        var since = started.GetProperty("total_lines").GetInt64();

        h.Session.FakeTerminal.EmitLine("line2");
        h.Session.FakeTerminal.EmitLine("line3");
        await Task.Delay(50, ct);

        var chunk = await h.Call.CallAsync("attach_read", new { session_id = h.Session.SessionId.ToString(), since }, ct);

        chunk.GetProperty("text").GetString().Should().Be("line2\nline3", "attach_read should only return what arrived after the cursor, not line1 again");
        chunk.GetProperty("total_lines").GetInt64().Should().Be(3);
    }

    [Fact]
    public async Task AttachSendInput_DeliversRawBytesToTheSessionsTerminal_BypassingTheApprovalGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(ct);
        using var _ = cleanup;

        await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);
        await h.Call.CallAsync("attach_send_input", new { session_id = h.Session.SessionId.ToString(), data = "ls -la\n" }, ct);

        h.Session.FakeTerminal.SentInput.Should().ContainSingle().Which.Should().Be("ls -la\n");
    }

    [Fact]
    public async Task AttachStart_Twice_WithoutStop_ThrowsAlreadyAttached()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(ct);
        using var _ = cleanup;

        await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);

        var act = () => h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already attached*");
    }

    [Fact]
    public async Task AttachStop_ReleasesTheExclusiveLock_SoASecondAttachStartSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(ct);
        using var _ = cleanup;

        await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);
        await h.Call.CallAsync("attach_stop", new { session_id = h.Session.SessionId.ToString() }, ct);

        var act = () => h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);

        await act.Should().NotThrowAsync();
    }
}
