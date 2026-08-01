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
/// Covers the framebuffer_* MCP tools' dispatch/capability-checking logic in <see cref="McpBridgeService"/>
/// against a fake <see cref="IFramebufferChannel"/> (a VNC-shaped session) over a real pipe connection —
/// the actual Win32/GDI+ RDP capture and X11-keysym VNC conversions are covered separately
/// (<c>Win32NativeWindowInputTests</c>, <c>VncPixelFormatTests</c>); this checks that
/// <c>McpBridgeService</c> routes framebuffer_screenshot/click/type/key/resize correctly, gates the
/// state-changing ones, and rejects sessions with no graphical-desktop capability at all.
/// </summary>
[Collection("AppSettings")]
public sealed class McpBridgeServiceFramebufferToolsTests
{
    private sealed class FakeFramebufferChannel : IFramebufferChannel
    {
        public FramebufferFormat Format => FramebufferFormat.Rgba32;
        public int Width => 2;
        public int Height => 2;
#pragma warning disable CS0067 // required by IFramebufferChannel; never raised in this fake
        public event EventHandler<FramebufferRegionUpdatedEventArgs>? RegionUpdated;
        public event EventHandler<ClipboardPayload>? ClipboardReceived;
#pragma warning restore CS0067
        public List<(PointerEvent Event, DateTime At)> PointerEvents { get; } = [];
        public List<KeyEvent> KeyEvents { get; } = [];
        public (int Width, int Height)? LastResize { get; private set; }

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

        // 2x2 RGBA32: distinct bytes so a real PNG round-trip would be verifiable if needed.
        public byte[]? CaptureSnapshot() =>
        [
            0x10, 0x20, 0x30, 0xFF,  0x11, 0x21, 0x31, 0xFF,
            0x12, 0x22, 0x32, 0xFF,  0x13, 0x23, 0x33, 0xFF,
        ];
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

    private static (McpBridgeService Service, Mock<ISessionManager> Sessions, string PipeName) MakeService(IApprovalGate gate)
    {
        // Short prefix + truncated GUID (not the full 32 hex chars) - macOS CI runners now use a
        // $TMPDIR long enough that 'CoreFxPipe_polyterm-...-<32 hex chars>' pushed the full Unix
        // domain socket path past .NET's 104-byte UnixDomainSocketEndPoint limit, failing with
        // ArgumentOutOfRangeException before the pipe ever connected - confirmed the hard way in CI.
        var pipeName = $"pt-{Guid.NewGuid():N}"[..15];
        var sessions = new Mock<ISessionManager>();
        var profiles = new Mock<IProfileStore>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());

        var service = new McpBridgeService(sessions.Object, profiles.Object, gate, NullLogger<McpBridgeService>.Instance, pipeName);
        return (service, sessions, pipeName);
    }

    private static async Task<JsonElement> CallAsync(string pipeName, string method, object @params)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var reader = new StreamReader(client, noBomUtf8, leaveOpen: true);
        var writer = new StreamWriter(client, noBomUtf8, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = 1, method, @params }));
        var line = await reader.ReadLineAsync();
        using var doc = JsonDocument.Parse(line!);
        return doc.RootElement.Clone();
    }

    // AvaloniaFact, not Fact: framebuffer_screenshot's PNG encoding runs via
    // Dispatcher.UIThread.InvokeAsync (McpBridgeService.EncodePngBase64Async) — without a headless
    // Avalonia dispatcher pump running (which only [AvaloniaFact] sets up), that call hangs forever
    // waiting for a UI thread that never processes queued work. Confirmed this exact hang for real:
    // this test was originally [Fact] and hung the whole test run indefinitely until diagnosed.
    //
    // Does NOT assert image_base64 is non-empty: confirmed separately
    // (WriteableBitmapHeadlessSaveDiagnosticTests) that Avalonia.Headless's stub renderer produces
    // zero PNG bytes from WriteableBitmap.Save() — a real environment limitation, not a product bug.
    // This test instead verifies everything that IS observable under headless: the call succeeds
    // (doesn't hang, doesn't error), reaches real encoding, and reports the correct dimensions.
    [AvaloniaFact]
    public async Task FramebufferScreenshot_ReturnsImageBase64Key_AndCorrectDimensions_ForASessionWithAFramebuffer()
    {
        var (service, sessions, pipeName) = MakeService(Mock.Of<IApprovalGate>());
        var channel = new FakeFramebufferChannel();
        var session = new FakeGraphicalSession { Profile = new ConnectionProfile { Name = "vnc-box", ProtocolId = "vnc" }, Framebuffer = channel };
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "framebuffer_screenshot", new { session_id = session.SessionId.ToString() });

            root.TryGetProperty("error", out _).Should().BeFalse();
            root.GetProperty("result").TryGetProperty("image_base64", out _).Should().BeTrue();
            root.GetProperty("result").GetProperty("width").GetInt32().Should().Be(2);
        }
        finally { service.Stop(); service.Dispose(); }
    }

    [Fact]
    public async Task FramebufferClick_SendsPressThenRelease_AndGoesThroughTheApprovalGate()
    {
        var gate = new Mock<IApprovalGate>();
        gate.Setup(g => g.CheckAsync(McpAction.FramebufferClick, It.IsAny<ConnectionProfile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var (service, sessions, pipeName) = MakeService(gate.Object);
        var channel = new FakeFramebufferChannel();
        var session = new FakeGraphicalSession { Profile = new ConnectionProfile { Name = "vnc-box", ProtocolId = "vnc" }, Framebuffer = channel };
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "framebuffer_click", new { session_id = session.SessionId.ToString(), x = 5, y = 7, button = "right" });

            root.TryGetProperty("error", out _).Should().BeFalse();
            channel.PointerEvents.Should().HaveCount(2);
            channel.PointerEvents[0].Event.Kind.Should().Be(PointerEventKind.Press);
            channel.PointerEvents[1].Event.Kind.Should().Be(PointerEventKind.Release);
            channel.PointerEvents[0].Event.Button.Should().Be(PointerButton.Right);
            channel.PointerEvents[0].Event.X.Should().Be(5);
            channel.PointerEvents[0].Event.Y.Should().Be(7);
            gate.Verify(g => g.CheckAsync(McpAction.FramebufferClick, It.IsAny<ConnectionProfile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { service.Stop(); service.Dispose(); }
    }

    [Fact]
    public async Task FramebufferClick_IsDenied_WhenTheApprovalGateRefuses()
    {
        var gate = new Mock<IApprovalGate>();
        gate.Setup(g => g.CheckAsync(McpAction.FramebufferClick, It.IsAny<ConnectionProfile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var (service, sessions, pipeName) = MakeService(gate.Object);
        var channel = new FakeFramebufferChannel();
        var session = new FakeGraphicalSession { Profile = new ConnectionProfile { Name = "vnc-box", ProtocolId = "vnc" }, Framebuffer = channel };
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "framebuffer_click", new { session_id = session.SessionId.ToString(), x = 1, y = 1 });

            root.TryGetProperty("error", out var error).Should().BeTrue();
            error.GetString().Should().Contain("Denied");
            channel.PointerEvents.Should().BeEmpty("a denied click should never reach the channel");
        }
        finally { service.Stop(); service.Dispose(); }
    }

    [Fact]
    public async Task FramebufferType_SendsPressAndReleaseKeyEvents_PerCharacter()
    {
        var gate = new Mock<IApprovalGate>();
        gate.Setup(g => g.CheckAsync(McpAction.FramebufferType, It.IsAny<ConnectionProfile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var (service, sessions, pipeName) = MakeService(gate.Object);
        var channel = new FakeFramebufferChannel();
        var session = new FakeGraphicalSession { Profile = new ConnectionProfile { Name = "vnc-box", ProtocolId = "vnc" }, Framebuffer = channel };
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "framebuffer_type", new { session_id = session.SessionId.ToString(), text = "hi" });

            root.TryGetProperty("error", out _).Should().BeFalse();
            channel.KeyEvents.Should().HaveCount(4); // press+release for each of 'h' and 'i'
            channel.KeyEvents[0].KeySymbol.Should().Be(((int)'h').ToString(System.Globalization.CultureInfo.InvariantCulture));
            channel.KeyEvents[0].Kind.Should().Be(KeyEventKind.Press);
            channel.KeyEvents[1].Kind.Should().Be(KeyEventKind.Release);
        }
        finally { service.Stop(); service.Dispose(); }
    }

    [Fact]
    public async Task FramebufferResize_CallsResizeAsync_AndIsNotGated()
    {
        var (service, sessions, pipeName) = MakeService(Mock.Of<IApprovalGate>(MockBehavior.Strict));
        var channel = new FakeFramebufferChannel();
        var session = new FakeGraphicalSession { Profile = new ConnectionProfile { Name = "vnc-box", ProtocolId = "vnc" }, Framebuffer = channel };
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "framebuffer_resize", new { session_id = session.SessionId.ToString(), width = 1024, height = 768 });

            root.TryGetProperty("error", out _).Should().BeFalse("resize should succeed without needing the (strict, unconfigured) approval gate at all");
            channel.LastResize.Should().Be((1024, 768));
        }
        finally { service.Stop(); service.Dispose(); }
    }

    [Fact]
    public async Task FramebufferScreenshot_ReturnsAClearError_ForASessionWithNoGraphicalDesktop()
    {
        var (service, sessions, pipeName) = MakeService(Mock.Of<IApprovalGate>());
        var session = new FakeGraphicalSession { Profile = new ConnectionProfile { Name = "ssh-box", ProtocolId = "ssh" } }; // no Framebuffer, no NativeHost
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "framebuffer_screenshot", new { session_id = session.SessionId.ToString() });

            root.TryGetProperty("error", out var error).Should().BeTrue();
            error.GetString().Should().Contain("no graphical desktop");
        }
        finally { service.Stop(); service.Dispose(); }
    }
}
