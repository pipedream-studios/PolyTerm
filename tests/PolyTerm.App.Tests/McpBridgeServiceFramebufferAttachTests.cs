using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PolyTerm.App.McpBridge;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the <c>attach_framebuffer_read</c>/<c>attach_framebuffer_send_pointer</c>/
/// <c>attach_framebuffer_send_key</c>/<c>attach_framebuffer_resize</c> pipe methods, and
/// <c>attach_start</c>/<c>attach_stop</c>'s capability-branching for framebuffer sessions, added for
/// issue #115 (Phase 3 of the daemon-mode/reattach line of work — server-side plumbing only, no CLI UX
/// this pass). Talks to a real <see cref="McpBridgeService"/> over a real named pipe, same discipline as
/// <see cref="McpBridgeServiceAttachTests"/> (the terminal-side equivalent) and
/// <see cref="McpBridgeServiceFramebufferToolsTests"/> (the existing gated-tools equivalent, whose fake
/// channel shape this file's <see cref="FakeFramebufferChannel"/> mirrors).
/// </summary>
[Collection("AppSettings")]
public sealed class McpBridgeServiceFramebufferAttachTests
{
    private sealed class FakeFramebufferChannel : IFramebufferChannel
    {
        public FramebufferFormat Format => FramebufferFormat.Rgba32;
        public int Width => 2;
        public int Height => 2;
        public event EventHandler<FramebufferRegionUpdatedEventArgs>? RegionUpdated;
        public event EventHandler<ClipboardPayload>? ClipboardReceived { add { } remove { } }
        public List<(PointerEvent Event, DateTime At)> PointerEvents { get; } = [];
        public List<KeyEvent> KeyEvents { get; } = [];
        public (int Width, int Height)? LastResize { get; private set; }

        private byte[]? _snapshot =
        [
            0x10, 0x20, 0x30, 0xFF,  0x11, 0x21, 0x31, 0xFF,
            0x12, 0x22, 0x32, 0xFF,  0x13, 0x23, 0x33, 0xFF,
        ];

        public ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default)
        {
            PointerEvents.Add((e, DateTime.UtcNow));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default)
        {
            KeyEvents.Add(e);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default)
        {
            LastResize = (width, height);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendClipboardAsync(ClipboardPayload payload, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendCtrlAltDeleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public byte[]? CaptureSnapshot() => _snapshot;

        /// <summary>Simulates a server-side frame change: mutates what <see cref="CaptureSnapshot"/> returns and fires <see cref="RegionUpdated"/>, exactly as VncFramebufferChannel/RdpFreerdpFramebufferChannel do at the same point (per issue #115's own research).</summary>
        public void ChangeFrame(byte fill)
        {
            _snapshot = Enumerable.Repeat(fill, 16).ToArray();
            RegionUpdated?.Invoke(this, new FramebufferRegionUpdatedEventArgs { X = 0, Y = 0, Width = Width, Height = Height, Data = _snapshot });
        }
    }

    private sealed class FakeGraphicalSession : IProtocolSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public required ConnectionProfile Profile { get; init; }
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<SessionErrorEventArgs>? ErrorOccurred { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ITerminalChannel? Terminal => null;
        public IFileTransferChannel? FileTransfer => null;
        public IFramebufferChannel? Framebuffer { get; init; }
        public ITunnelManager? Tunnels => null;
        public INativeHostChannel? NativeHost => null;
        public IWebChannel? Web => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Harness(McpBridgeService Service, FakeGraphicalSession Session, FakeFramebufferChannel Channel, PipeCaller Call);

    /// <summary>Small round-trip helper over a real <see cref="NamedPipeClientStream"/>, matching <see cref="McpBridgeServiceAttachTests"/>'s own <c>PipeCaller</c>.</summary>
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

    private static async Task<(Harness Harness, IDisposable Cleanup)> StartAsync(IApprovalGate gate, string protocolId, CancellationToken ct)
    {
        var pipeName = $"polyterm-test-{Guid.NewGuid():N}";
        var sessions = new Mock<ISessionManager>();
        var profiles = new Mock<IProfileStore>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());

        var service = new McpBridgeService(sessions.Object, profiles.Object, gate, NullLogger<McpBridgeService>.Instance, pipeName);
        service.Start();
        await Task.Delay(200, ct).ConfigureAwait(false);

        var channel = new FakeFramebufferChannel();
        var session = new FakeGraphicalSession { Profile = new ConnectionProfile { Name = "attach-fb-test", ProtocolId = protocolId }, Framebuffer = channel };
        sessions.Raise(s => s.SessionOpened += null, sessions.Object, new SessionEventArgs { Session = session });

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

        var caller = new PipeCaller(pipe);
        return (new Harness(service, session, channel, caller), new CombinedDisposable(pipe, service));
    }

    private static Task<(Harness Harness, IDisposable Cleanup)> StartAsync(CancellationToken ct) =>
        StartAsync(Mock.Of<IApprovalGate>(), "vnc", ct);

    private sealed class CombinedDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var i in items) i.Dispose(); }
    }

    [AvaloniaFact]
    public async Task AttachStart_OnAFramebufferSession_ReturnsKindFramebuffer_AndAnInitialFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(ct);
        using var __ = cleanup;

        var result = await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);

        result.GetProperty("kind").GetString().Should().Be("framebuffer");
        result.TryGetProperty("image_base64", out _).Should().BeTrue();
        result.GetProperty("width").GetInt32().Should().Be(2);
        result.GetProperty("height").GetInt32().Should().Be(2);
    }

    [AvaloniaFact]
    public async Task AttachFramebufferRead_ReturnsChangedFalse_WhenNothingChangedSinceLastVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(ct);
        using var __ = cleanup;

        var started = await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);
        var version = started.GetProperty("version").GetInt64();

        var polled = await h.Call.CallAsync("attach_framebuffer_read", new { session_id = h.Session.SessionId.ToString(), since_version = version }, ct);

        polled.GetProperty("changed").GetBoolean().Should().BeFalse();
        polled.TryGetProperty("image_base64", out _).Should().BeFalse("no encode should happen at all when nothing changed");
    }

    [AvaloniaFact]
    public async Task AttachFramebufferRead_ReturnsANewFrame_AfterRegionUpdatedFires()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(ct);
        using var __ = cleanup;

        var started = await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);
        var version = started.GetProperty("version").GetInt64();

        h.Channel.ChangeFrame(0xAA);

        var polled = await h.Call.CallAsync("attach_framebuffer_read", new { session_id = h.Session.SessionId.ToString(), since_version = version }, ct);

        polled.TryGetProperty("image_base64", out _).Should().BeTrue();
        polled.GetProperty("version").GetInt64().Should().BeGreaterThan(version);
    }

    [AvaloniaFact]
    public async Task AttachFramebufferSendPointer_ReachesTheFakeChannel_AsOneEventNotAPressReleasePair()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(Mock.Of<IApprovalGate>(MockBehavior.Strict), "vnc", ct);
        using var __ = cleanup;

        await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);
        await h.Call.CallAsync("attach_framebuffer_send_pointer", new { session_id = h.Session.SessionId.ToString(), x = 5, y = 7, button = "right", kind = "press" }, ct);

        h.Channel.PointerEvents.Should().ContainSingle("attach input sends one raw event per call, unlike the gated framebuffer_click's implicit press+release pair");
        h.Channel.PointerEvents[0].Event.Kind.Should().Be(PointerEventKind.Press);
        h.Channel.PointerEvents[0].Event.Button.Should().Be(PointerButton.Right);
    }

    [AvaloniaFact]
    public async Task AttachFramebufferSendKey_ReachesTheFakeChannel_UngatedAndAsASingleEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(Mock.Of<IApprovalGate>(MockBehavior.Strict), "vnc", ct);
        using var __ = cleanup;

        await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);
        await h.Call.CallAsync("attach_framebuffer_send_key", new { session_id = h.Session.SessionId.ToString(), keysym = "65", kind = "press" }, ct);

        h.Channel.KeyEvents.Should().ContainSingle();
        h.Channel.KeyEvents[0].Kind.Should().Be(KeyEventKind.Press);
        h.Channel.KeyEvents[0].KeySymbol.Should().Be("65");
    }

    [AvaloniaFact]
    public async Task AttachFramebufferResize_ReachesTheFakeChannel_Ungated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(Mock.Of<IApprovalGate>(MockBehavior.Strict), "vnc", ct);
        using var __ = cleanup;

        await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);
        await h.Call.CallAsync("attach_framebuffer_resize", new { session_id = h.Session.SessionId.ToString(), width = 1024, height = 768 }, ct);

        h.Channel.LastResize.Should().Be((1024, 768));
    }

    [AvaloniaFact]
    public async Task AttachStart_OnAFramebufferSession_Twice_ThrowsAlreadyAttached()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(ct);
        using var __ = cleanup;

        await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);

        var act = () => h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already attached*");
    }

    [AvaloniaFact]
    public async Task AttachStop_OnAFramebufferSession_ReleasesTheLock_SoASecondAttachStartSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, cleanup) = await StartAsync(ct);
        using var __ = cleanup;

        await h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);
        await h.Call.CallAsync("attach_stop", new { session_id = h.Session.SessionId.ToString() }, ct);

        var act = () => h.Call.CallAsync("attach_start", new { session_id = h.Session.SessionId.ToString() }, ct);

        await act.Should().NotThrowAsync();
    }

    [AvaloniaFact]
    public async Task AttachStart_OnASessionWithNeitherTerminalNorFramebuffer_ThrowsAndDoesNotBlockAFutureAttach()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"polyterm-test-{Guid.NewGuid():N}";
        var sessions = new Mock<ISessionManager>();
        var profiles = new Mock<IProfileStore>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());

        using var service = new McpBridgeService(sessions.Object, profiles.Object, Mock.Of<IApprovalGate>(), NullLogger<McpBridgeService>.Instance, pipeName);
        service.Start();
        await Task.Delay(200, ct);

        var session = new FakeGraphicalSession { Profile = new ConnectionProfile { Name = "web-only", ProtocolId = "http" } }; // no Terminal, no Framebuffer, no NativeHost
        sessions.Raise(s => s.SessionOpened += null, sessions.Object, new SessionEventArgs { Session = session });

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await pipe.ConnectAsync(connectCts.Token);
        using var caller = new PipeCaller(pipe);

        var firstAttempt = () => caller.CallAsync("attach_start", new { session_id = session.SessionId.ToString() }, ct);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("*neither a terminal nor a graphical desktop*");

        // The session genuinely has no attachable capability, so a second attempt should fail the same
        // way both times - the regression this guards against is it instead failing with "already
        // attached", which would mean the first failed attempt never rolled back its exclusivity claim.
        var secondAttempt = () => caller.CallAsync("attach_start", new { session_id = session.SessionId.ToString() }, ct);
        await secondAttempt.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*neither a terminal nor a graphical desktop*", "a rolled-back claim should fail the same way again, not with 'already attached'");
    }
}
