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
/// Covers the webview_* MCP tools' dispatch/capability-checking logic in <see cref="McpBridgeService"/>
/// against a fake <see cref="IWebChannel"/>. webview_screenshot/webview_invoke_script's real behavior
/// needs a live WebView2 control (<see cref="PolyTerm.App.Surfaces.Web.WebSurface"/>'s <c>IWebViewHost</c>
/// side) and isn't covered here — this checks that <c>McpBridgeService</c> routes
/// webview_navigate/get_url/invoke_script correctly, gates the state-changing ones, and rejects
/// sessions with no embedded web view at all.
/// </summary>
[Collection("AppSettings")]
public sealed class McpBridgeServiceWebToolsTests
{
    private sealed class FakeWebChannel : IWebChannel
    {
        public Uri CurrentUri { get; private set; }
#pragma warning disable CS0067 // required by IWebChannel; never raised in this fake
        public event EventHandler<Uri>? Navigated;
#pragma warning restore CS0067
        public List<Uri> NavigatedTo { get; } = [];
        public string? LastScript { get; private set; }

        public FakeWebChannel(Uri initialUri) => CurrentUri = initialUri;

        public Task NavigateAsync(Uri uri, CancellationToken ct = default)
        {
            CurrentUri = uri;
            NavigatedTo.Add(uri);
            return Task.CompletedTask;
        }

        public void AttachHost(IWebViewHost? host) { }
        public void ReportNavigated(Uri uri) => CurrentUri = uri;

        public Task<string?> InvokeScriptAsync(string script, CancellationToken ct = default)
        {
            LastScript = script;
            return Task.FromResult<string?>("script-result");
        }

        public Task<byte[]?> CaptureScreenshotAsync(CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
    }

    private sealed class FakeWebSession : IProtocolSession
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
        public IFramebufferChannel? Framebuffer => null;
        public ITunnelManager? Tunnels => null;
        public INativeHostChannel? NativeHost => null;
        public IWebChannel? Web { get; init; }
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

    [Fact]
    public async Task WebGetUrl_ReturnsTheChannelsCurrentUri_AndIsNotGated()
    {
        var (service, sessions, pipeName) = MakeService(Mock.Of<IApprovalGate>(MockBehavior.Strict));
        var channel = new FakeWebChannel(new Uri("https://example.com"));
        var session = new FakeWebSession { Profile = new ConnectionProfile { Name = "web-box", ProtocolId = "https" }, Web = channel };
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "webview_get_url", new { session_id = session.SessionId.ToString() });

            root.TryGetProperty("error", out _).Should().BeFalse("get_url should succeed without needing the (strict, unconfigured) approval gate at all");
            root.GetProperty("result").GetProperty("url").GetString().Should().Be("https://example.com/");
        }
        finally { service.Stop(); service.Dispose(); }
    }

    [Fact]
    public async Task WebNavigate_CallsTheChannel_AndGoesThroughTheApprovalGate()
    {
        var gate = new Mock<IApprovalGate>();
        gate.Setup(g => g.CheckAsync(McpAction.WebNavigate, It.IsAny<ConnectionProfile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var (service, sessions, pipeName) = MakeService(gate.Object);
        var channel = new FakeWebChannel(new Uri("https://example.com"));
        var session = new FakeWebSession { Profile = new ConnectionProfile { Name = "web-box", ProtocolId = "https" }, Web = channel };
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "webview_navigate", new { session_id = session.SessionId.ToString(), url = "https://example.org/path" });

            root.TryGetProperty("error", out _).Should().BeFalse();
            channel.NavigatedTo.Should().ContainSingle().Which.Should().Be(new Uri("https://example.org/path"));
            gate.Verify(g => g.CheckAsync(McpAction.WebNavigate, It.IsAny<ConnectionProfile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { service.Stop(); service.Dispose(); }
    }

    [Fact]
    public async Task WebNavigate_IsDenied_WhenTheApprovalGateRefuses()
    {
        var gate = new Mock<IApprovalGate>();
        gate.Setup(g => g.CheckAsync(McpAction.WebNavigate, It.IsAny<ConnectionProfile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var (service, sessions, pipeName) = MakeService(gate.Object);
        var channel = new FakeWebChannel(new Uri("https://example.com"));
        var session = new FakeWebSession { Profile = new ConnectionProfile { Name = "web-box", ProtocolId = "https" }, Web = channel };
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "webview_navigate", new { session_id = session.SessionId.ToString(), url = "https://example.org/" });

            root.TryGetProperty("error", out var error).Should().BeTrue();
            error.GetString().Should().Contain("Denied");
            channel.NavigatedTo.Should().BeEmpty("a denied navigate should never reach the channel");
        }
        finally { service.Stop(); service.Dispose(); }
    }

    [Fact]
    public async Task WebInvokeScript_ReturnsTheChannelsResult_AndGoesThroughTheApprovalGate()
    {
        var gate = new Mock<IApprovalGate>();
        gate.Setup(g => g.CheckAsync(McpAction.WebInvokeScript, It.IsAny<ConnectionProfile>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var (service, sessions, pipeName) = MakeService(gate.Object);
        var channel = new FakeWebChannel(new Uri("https://example.com"));
        var session = new FakeWebSession { Profile = new ConnectionProfile { Name = "web-box", ProtocolId = "https" }, Web = channel };
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "webview_invoke_script", new { session_id = session.SessionId.ToString(), script = "document.title" });

            root.TryGetProperty("error", out _).Should().BeFalse();
            root.GetProperty("result").GetProperty("result").GetString().Should().Be("script-result");
            channel.LastScript.Should().Be("document.title");
        }
        finally { service.Stop(); service.Dispose(); }
    }

    [Fact]
    public async Task WebGetUrl_ReturnsAClearError_ForASessionWithNoWebView()
    {
        var (service, sessions, pipeName) = MakeService(Mock.Of<IApprovalGate>());
        var session = new FakeWebSession { Profile = new ConnectionProfile { Name = "ssh-box", ProtocolId = "ssh" } }; // no Web
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            sessions.Raise(s => s.SessionOpened += null, new SessionEventArgs { Session = session });

            var root = await CallAsync(pipeName, "webview_get_url", new { session_id = session.SessionId.ToString() });

            root.TryGetProperty("error", out var error).Should().BeTrue();
            error.GetString().Should().Contain("no embedded web view");
        }
        finally { service.Stop(); service.Dispose(); }
    }
}
