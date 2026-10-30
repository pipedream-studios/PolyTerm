using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PolyTerm.App.McpBridge;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers what <c>terminal_execute</c> actually writes to the session's terminal, and how it decides a command
/// has finished (<see cref="ShellSentinel"/>). It used to terminate
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

        /// <summary>
        /// What the "shell" does with each line sent (minus the "\r"): returns the output to write back, and may
        /// call <see cref="Emit"/> later for output that arrives after a pause. Defaults to echo + "ok".
        /// </summary>
        public Func<string, string> Respond { get; set; } = line => line + "\r\nok\r\n";

#pragma warning disable CS0067
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
#pragma warning restore CS0067
        public string TerminalType => "xterm-256color";

        public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var text = Encoding.UTF8.GetString(data.Span);
            SentInput.Add(text);
            Emit(Respond(text.TrimEnd('\r')));
            return ValueTask.CompletedTask;
        }

        public void Emit(string output) => _sink?.Invoke(Encoding.UTF8.GetBytes(output));

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

    private sealed record Harness(McpBridgeService Service, FakeTerminalChannel Terminal, FakeProtocolSession Session,
                                  NamedPipeClientStream Pipe, StreamReader Reader, StreamWriter Writer) : IDisposable
    {
        private int _nextId;

        public async Task<JsonElement> ExecuteAsync(string command, int timeoutSeconds = 5, string? shell = null)
        {
            var ct = TestContext.Current.CancellationToken;
            object args = shell is null
                ? new { session_id = Session.SessionId.ToString(), command, timeout_seconds = timeoutSeconds }
                : new { session_id = Session.SessionId.ToString(), command, timeout_seconds = timeoutSeconds, shell };
            var request = new { id = ++_nextId, method = "terminal_execute", @params = args };
            await Writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), ct);
            var line = await Reader.ReadLineAsync(ct) ?? throw new EndOfStreamException();
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.GetProperty("result").Clone();
        }

        public void Dispose()
        {
            Reader.Dispose();
            Writer.Dispose();
            Pipe.Dispose();
            Service.Dispose();
        }
    }

    private static async Task<Harness> StartAsync()
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

        var service = new McpBridgeService(sessions.Object, profiles.Object, gate.Object, NullLogger<McpBridgeService>.Instance, pipeName);
        service.Start();
        await Task.Delay(200, ct);

        var terminal = new FakeTerminalChannel();
        var session = new FakeProtocolSession { Profile = new ConnectionProfile { Name = "exec-test", ProtocolId = "ssh" }, FakeTerminal = terminal };
        sessions.Raise(s => s.SessionOpened += null, sessions.Object, new SessionEventArgs { Session = session });

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await pipe.ConnectAsync(connectCts.Token);

        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        return new Harness(service, terminal, session, pipe, reader, writer);
    }

    /// <summary>
    /// A bash-like responder: answers the shell probe as bash would, and for a marked command echoes it, then
    /// after <paramref name="pause"/> prints <paramref name="output"/> and the marker with <paramref name="exitCode"/>.
    /// </summary>
    private static Func<string, string> Bash(FakeTerminalChannel terminal, TimeSpan pause, string output, int exitCode) => line =>
    {
        var probe = Regex.Match(line, @"__PT_SH_(\w+)_\$FISH_VERSION");
        if (probe.Success)
            return $"$ {line}\r\n__PT_SH_{probe.Groups[1].Value}_/\r\n$ ";

        var marker = Regex.Match(line, @"__PT_(\w+)_\$\?__");
        if (!marker.Success)
            return $"{line}\r\n";
        _ = Task.Run(async () =>
        {
            await Task.Delay(pause);
            terminal.Emit($"{output}\r\n__PT_{marker.Groups[1].Value}_{exitCode}__\r\n$ ");
        });
        return $"{line}\r\n";
    };

    [Fact]
    public async Task TerminalExecute_TerminatesTheCommandWithCarriageReturn_AndReturnsItsOutput()
    {
        using var h = await StartAsync();

        var result = await h.ExecuteAsync("hostname", shell: "none");

        h.Terminal.SentInput.Should().ContainSingle().Which.Should().Be("hostname\r");
        result.GetProperty("text").GetString().Should().Be("hostname\nok");
        result.GetProperty("exit_code").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TerminalExecute_WaitsForTheMarker_ThroughAPause_AndReportsTheExitCode()
    {
        using var h = await StartAsync();
        h.Terminal.Respond = Bash(h.Terminal, TimeSpan.FromMilliseconds(1200), "partial results", exitCode: 3);

        var result = await h.ExecuteAsync("sleep 1; false");

        result.GetProperty("timed_out").GetBoolean().Should().BeFalse();
        result.GetProperty("exit_code").GetInt32().Should().Be(3);
        result.GetProperty("shell").GetString().Should().Be("posix");
        result.GetProperty("text").GetString().Should().Be("$ sleep 1; false\npartial results",
            "the 1.2s pause is longer than the old 500ms quiet window, and the marker is removed from both the echo and the output");
        h.Terminal.SentInput.Should().HaveCount(2);
        h.Terminal.SentInput[0].Should().StartWith("echo \"__PT_SH_");
        h.Terminal.SentInput[1].Should().MatchRegex(@"^sleep 1; false; echo ""__PT_\w+_\$\?__""\r$");
    }

    [Fact]
    public async Task TerminalExecute_ProbesOncePerSession_UntilACommandSwitchesShells()
    {
        using var h = await StartAsync();
        h.Terminal.Respond = Bash(h.Terminal, TimeSpan.Zero, "ok", exitCode: 0);

        await h.ExecuteAsync("uptime");
        await h.ExecuteAsync("whoami");
        h.Terminal.SentInput.Count(s => s.Contains("__PT_SH_")).Should().Be(1, "the shell kind is cached after the first probe");

        await h.ExecuteAsync("sudo -i");
        h.Terminal.SentInput.Last().Should().Be("sudo -i\r", "a marker would only run once the root shell exited");
        await h.ExecuteAsync("whoami");
        h.Terminal.SentInput.Count(s => s.Contains("__PT_SH_")).Should().Be(2, "sudo -i may have started a different shell");
    }

    [Fact]
    public async Task TerminalExecute_OnTimeout_ReturnsTheUnfinishedLine_SoAPromptForInputIsVisible()
    {
        using var h = await StartAsync();
        var bash = Bash(h.Terminal, TimeSpan.Zero, "", 0);
        h.Terminal.Respond = line => line.Contains("__PT_SH_") ? bash(line) : $"{line}\r\n[sudo] password for rob: ";

        var result = await h.ExecuteAsync("sudo whoami", timeoutSeconds: 1);

        result.GetProperty("timed_out").GetBoolean().Should().BeTrue();
        result.GetProperty("exit_code").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("text").GetString().Should().Be("$ sudo whoami\n[sudo] password for rob: ");
    }

    [Fact]
    public async Task TerminalExecute_UnrecognisedShell_FallsBackToWaitingForOutputToSettle()
    {
        using var h = await StartAsync();
        h.Terminal.Respond = line => $"{line}\r\n% Invalid input detected\r\nrouter# ";

        var result = await h.ExecuteAsync("show version");

        result.GetProperty("shell").GetString().Should().Be("unknown");
        result.GetProperty("exit_code").ValueKind.Should().Be(JsonValueKind.Null);
        h.Terminal.SentInput.Last().Should().Be("show version\r", "no marker is appended for a shell that isn't recognised");
    }
}
