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
/// Covers what <c>terminal_execute</c> actually writes to the session's terminal. It used to terminate
/// commands with "\n", which a Windows ConPTY (Windows OpenSSH's shell) treats as Ctrl+J rather than
/// Enter - the command was typed at the prompt and never run. Same real-named-pipe approach as
/// <see cref="McpBridgeServiceAttachTests"/>.
/// </summary>
[Collection("AppSettings")]
public sealed class McpBridgeServiceTerminalExecuteTests
{
    private sealed class FakeTerminalChannel : ITerminalChannel
    {
        private Action<ReadOnlyMemory<byte>>? _sink;
        public List<string> SentInput { get; } = [];

#pragma warning disable CS0067
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
#pragma warning restore CS0067
        public string TerminalType => "xterm-256color";

        /// <summary>Echoes the input back as output, like a remote shell would, so terminal_execute has something to return.</summary>
        public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var text = Encoding.UTF8.GetString(data.Span);
            SentInput.Add(text);
            _sink?.Invoke(Encoding.UTF8.GetBytes(text.TrimEnd('\r') + "\r\nok\r\n"));
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default) => ValueTask.CompletedTask;

        public void RegisterOutputSink(Action<ReadOnlyMemory<byte>> sink) => _sink = sink;
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

    [Fact]
    public async Task TerminalExecute_TerminatesTheCommandWithCarriageReturn_AndReturnsItsOutput()
    {
        var ct = TestContext.Current.CancellationToken;

        // Short prefix + truncated GUID - see McpBridgeServiceAttachTests.StartAsync for the macOS socket path limit.
        var pipeName = $"pt-{Guid.NewGuid():N}"[..15];
        var sessions = new Mock<ISessionManager>();
        var profiles = new Mock<IProfileStore>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        var gate = new Mock<IApprovalGate>();
        gate.Setup(g => g.CheckAsync(It.IsAny<McpAction>(), It.IsAny<ConnectionProfile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var service = new McpBridgeService(sessions.Object, profiles.Object, gate.Object, NullLogger<McpBridgeService>.Instance, pipeName);
        service.Start();
        await Task.Delay(200, ct);

        var terminal = new FakeTerminalChannel();
        var session = new FakeProtocolSession { Profile = new ConnectionProfile { Name = "exec-test", ProtocolId = "ssh" }, FakeTerminal = terminal };
        sessions.Raise(s => s.SessionOpened += null, sessions.Object, new SessionEventArgs { Session = session });

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await pipe.ConnectAsync(connectCts.Token);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var request = new { id = 1, method = "terminal_execute", @params = new { session_id = session.SessionId.ToString(), command = "hostname", timeout_seconds = 5 } };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), ct);
        var line = await reader.ReadLineAsync(ct) ?? throw new EndOfStreamException();
        using var doc = JsonDocument.Parse(line);

        terminal.SentInput.Should().ContainSingle().Which.Should().Be("hostname\r");
        doc.RootElement.GetProperty("result").GetProperty("text").GetString().Should().Be("hostname\nok");
    }
}
