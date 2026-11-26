using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PolyTerm.App.McpBridge;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// What the MCP bridge does and logs when it can't create its pipe, as when another user holds the name
/// (issue #378). Another user can't be faked in a test, so a server that allows only one instance holds the
/// name instead: that makes creating the bridge's instances fail on every platform. The round trips read
/// <c>App.Settings</c>, hence the <c>AppSettings</c> collection.
/// </summary>
[Collection("AppSettings")]
public sealed class McpBridgeServicePipeCreationTests
{
    [Fact]
    public async Task PipeCantBeCreated_LogsOneWarning_ThenListensOnceTheNameIsFree()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"pt-{Guid.NewGuid():N}"[..15];
        var blocker = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var logger = new RecordingLogger();

        await using var service = MakeService(logger, pipeName);
        service.Start();

        // Four loops, so this is every one of them failing several times over.
        await WaitUntilAsync(() => logger.Entries.Count(IsRetry) >= 12, ct);

        var warning = logger.Entries.Where(e => e.Level >= LogLevel.Warning).Should().ContainSingle().Which;
        warning.Exception.Should().NotBeNull();
        warning.Message.Should().Contain("can't create the pipe").And.Contain(warning.Exception!.Message);
        logger.Entries.Where(IsRetry).Should().OnlyContain(e => e.Exception == null);
        logger.Entries.Should().NotContain(e => IsListening(e));

        await blocker.DisposeAsync();
        await WaitUntilAsync(() => logger.Entries.Any(IsListening), ct);

        await AssertRoundTripAsync(pipeName, ct);
        logger.Entries.Count(IsListening).Should().Be(1);
        logger.Entries.Count(e => e.Level >= LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public async Task RootedPipeName_RoundTripsThroughTheClientTheToolsUse()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A rooted pipe name is a socket path, which only macOS and Linux have.");
        var ct = TestContext.Current.CancellationToken;
        // The shape of the Linux default. /tmp rather than the temp directory, to stay under the socket path limit.
        var directory = Directory.CreateDirectory($"/tmp/pt-{Guid.NewGuid():N}"[..20]);
        try
        {
            var pipeName = Path.Combine(directory.FullName, "polyterm.sock");
            var logger = new RecordingLogger();
            await using (var service = MakeService(logger, pipeName))
            {
                service.Start();
                await WaitUntilAsync(() => logger.Entries.Any(IsListening), ct);

                File.Exists(pipeName).Should().BeTrue();
                await AssertRoundTripAsync(pipeName, ct);
            }

            File.Exists(pipeName).Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static bool IsListening(Entry entry) => entry.Message.StartsWith("MCP bridge: listening on", StringComparison.Ordinal);

    private static bool IsRetry(Entry entry) => entry.Message.StartsWith("MCP bridge: couldn't create a pipe instance", StringComparison.Ordinal);

    private static McpBridgeService MakeService(ILogger<McpBridgeService> logger, string pipeName)
    {
        var profiles = new Mock<IProfileStore>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        return new McpBridgeService(Mock.Of<ISessionManager>(), profiles.Object, Mock.Of<IApprovalGate>(), logger, pipeName)
        {
            InitialPipeRetryDelay = TimeSpan.FromMilliseconds(10),
            MaxPipeRetryDelay = TimeSpan.FromMilliseconds(50),
        };
    }

    private static async Task AssertRoundTripAsync(string pipeName, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        using var client = McpPipe.CreateClient(pipeName);
        await client.ConnectAsync(timeout.Token);
        McpPipe.EnsureServerIsCurrentUser(client);

        var noBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(client, noBom, leaveOpen: true);
        await using var writer = new StreamWriter(client, noBom, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("""{"id":1,"method":"connection_list","params":{}}""".AsMemory(), timeout.Token);

        using var response = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        response.RootElement.GetProperty("id").GetInt32().Should().Be(1);
        response.RootElement.TryGetProperty("result", out _).Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the bridge.");
            await Task.Delay(10, ct);
        }
    }

    private sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<McpBridgeService>
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public IReadOnlyCollection<Entry> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new Entry(logLevel, formatter(state, exception), exception));
    }
}
