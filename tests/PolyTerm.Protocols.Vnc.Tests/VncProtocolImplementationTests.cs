using System.Collections.Concurrent;
using AwesomeAssertions;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using MarcusW.VncClient.Protocol.MessageTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PolyTerm.Protocols.Vnc.Tests;

/// <summary>
/// What the replacement send and receive loops do when something fails on a connection that nobody is
/// closing: the connection must still be told, so it can report the interruption (issue #374).
/// </summary>
public sealed class VncProtocolImplementationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // An id the library doesn't use, for the test's own message types.
    private const byte TestMessageTypeId = 200;

    [Fact]
    public async Task ReceiveLoop_CancellationNobodyAskedFor_InterruptsTheConnection()
    {
        var cause = await CaptureInterruptionAsync(
            new VncProtocolImplementation(context =>
                DefaultImplementation.GetDefaultMessageTypes(context).Append(new CancellingIncomingMessageType())),
            serverMessage: TestMessageTypeId);

        cause.Should().BeOfType<IOException>().Which.InnerException.Should().BeOfType<OperationCanceledException>();
    }

    [Fact]
    public async Task SendLoop_CancellationNobodyAskedFor_InterruptsTheConnection()
    {
        var cause = await CaptureInterruptionAsync(
            new VncProtocolImplementation(context =>
                DefaultImplementation.GetDefaultMessageTypes(context).Append(new CancellingOutgoingMessageType())),
            afterSubscribing: connection => connection.EnqueueMessage(new CancellingMessage()));

        cause.Should().BeOfType<IOException>().Which.InnerException.Should().BeOfType<OperationCanceledException>();
    }

    [Fact]
    public async Task ReceiveLoop_UnknownMessageType_StillReportsUnexpectedData()
    {
        var cause = await CaptureInterruptionAsync(new VncProtocolImplementation(), serverMessage: TestMessageTypeId);

        cause.Should().BeOfType<UnexpectedDataException>();
    }

    [Fact]
    public async Task ReceiveLoop_LogsUnderTheLibraryCategory_SoTheAppsLogFiltersStillApply()
    {
        var logs = new CapturingLoggerFactory();

        await CaptureInterruptionAsync(new VncProtocolImplementation(), serverMessage: TestMessageTypeId, loggerFactory: logs);

        // The app's filters (issue #343) are by category prefix; this per-message line is one they hide.
        logs.Entries.Where(e => e.Message.StartsWith("Received message", StringComparison.Ordinal))
            .Should().NotBeEmpty()
            .And.OnlyContain(e => e.Category == "MarcusW.VncClient.Protocol.Implementation.Services.Communication.RfbMessageReceiver");
    }

    /// <summary>
    /// Connects with <paramref name="implementation"/>, then provokes a failure (a byte from the server,
    /// or something done to the connection) and returns what the connection says interrupted it.
    /// </summary>
    private static async Task<Exception> CaptureInterruptionAsync(
        VncProtocolImplementation implementation,
        byte? serverMessage = null,
        Action<RfbConnection>? afterSubscribing = null,
        ILoggerFactory? loggerFactory = null)
    {
        var provoke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRfbServer(4, 4, (1, 2, 3))
        {
            AfterUpdate = async (stream, _, ct) =>
            {
                await provoke.Task.WaitAsync(ct);
                if (serverMessage is { } message)
                    await stream.WriteAsync(new[] { message }, ct);
                await finished.Task.WaitAsync(ct);
            },
        };
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        server.Start(serverCts.Token);

        var connection = await new VncClient(loggerFactory ?? NullLoggerFactory.Instance, implementation).ConnectAsync(
            new ConnectParameters
            {
                TransportParameters   = new TcpTransportParameters { Host = "127.0.0.1", Port = server.Port },
                AuthenticationHandler = new PasswordAuthenticationHandler(""),
                MaxReconnectAttempts  = 0,
            },
            TestContext.Current.CancellationToken);
        try
        {
            var interrupted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RfbConnection.InterruptionCause) && connection.InterruptionCause is { } cause)
                    interrupted.TrySetResult(cause);
            };

            provoke.SetResult();
            afterSubscribing?.Invoke(connection);

            return await interrupted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            finished.TrySetResult();
            await connection.CloseAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
            connection.Dispose();
        }
    }

    private sealed class CancellingIncomingMessageType : IIncomingMessageType
    {
        public byte Id => TestMessageTypeId;
        public string Name => "CancellingTestMessage";
        public bool IsStandardMessageType => true;

        public void ReadMessage(ITransport transport, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();
    }

    private sealed class CancellingOutgoingMessageType : IOutgoingMessageType
    {
        public byte Id => TestMessageTypeId;
        public string Name => "CancellingTestMessage";
        public bool IsStandardMessageType => true;

        public void WriteToTransport(IOutgoingMessage<IOutgoingMessageType> message, ITransport transport, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();
    }

    private sealed class CancellingMessage : IOutgoingMessage<CancellingOutgoingMessageType>
    {
        public string? GetParametersOverview() => null;
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<(string Category, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<(string, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, formatter(state, exception)));
        }
    }
}
