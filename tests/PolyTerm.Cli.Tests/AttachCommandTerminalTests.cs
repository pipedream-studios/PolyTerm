using System.Text.Json;
using AwesomeAssertions;
using PolyTerm.Cli.Commands;
using Xunit;

namespace PolyTerm.Cli.Tests;

/// <summary>
/// Covers <c>polyterm attach</c>'s terminal-kind branch (<c>AttachToTerminalSessionAsync</c>, issue
/// #114/#116) — closes the gap flagged in issue #128: this path had zero test coverage before now,
/// unlike the framebuffer branch (<see cref="AttachCommandFramebufferTests"/>) or the raw pipe framing
/// (<see cref="AttachPipeClientTests"/>). The poll loop only exits via cancellation, so these tests
/// drive it with an explicit <see cref="CancellationTokenSource"/> passed through
/// <see cref="ConsoleCapture.RunAsync(System.CommandLine.Command, CancellationToken, string[])"/> to
/// simulate Ctrl+C — there's no real console in a test process to send a physical one to, and the
/// stdin-forwarding side-loop (reading <c>Console.OpenStandardInput()</c>) is deliberately not covered
/// here for the same reason <c>ConnectCommandTests</c> doesn't cover its own connect-success path: it's
/// fire-and-forget against a real OS stream a test can't safely script.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class AttachCommandTerminalTests : IAsyncDisposable
{
    private sealed record Call(string Method, JsonElement Params);

    /// <summary>How long a cancelled attach may take to return before the test fails rather than hangs (issue #321).</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    private readonly List<FakePipeServer> _servers = [];

    /// <summary>Disposes every fake server this test started, so none outlives the test host (issue #226). xunit v3 awaits IAsyncDisposable on test classes after each test.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers)
            await server.DisposeAsync();
    }

    /// <summary>Starts a fake server that dispatches by method name via <paramref name="respond"/>, recording every call it receives.</summary>
    private Task<(string PipeName, List<Call> Calls)> StartFakeServerAsync(
        Func<string, JsonElement, object> respond, CancellationToken ct)
    {
        var calls = new List<Call>();
        var server = FakePipeServer.Start(root =>
        {
            var id = root.GetProperty("id").GetInt32();
            var method = root.GetProperty("method").GetString()!;
            root.TryGetProperty("params", out var @params);

            lock (calls) calls.Add(new Call(method, @params.Clone()));

            object response;
            try
            {
                response = new { id, result = respond(method, @params) };
            }
            catch (InvalidOperationException ex)
            {
                response = new { id, error = ex.Message };
            }
            return JsonSerializer.Serialize(response);
        }, ct);
        _servers.Add(server);
        return Task.FromResult((server.PipeName, calls));
    }

    private static async Task<(int ExitCode, string Out, string Error)> RunAttachAsync(string pipeName, CancellationToken ct, params string[] args)
    {
        var previous = Environment.GetEnvironmentVariable("POLYTERM_PIPE");
        Environment.SetEnvironmentVariable("POLYTERM_PIPE", pipeName);
        try
        {
            var command = AttachCommand.Build();
            return await ConsoleCapture.RunAsync(command, ct, args);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POLYTERM_PIPE", previous);
        }
    }

    // These three tests used to arm a fixed wall-clock CancelAfter() and race it against the attach
    // command's own connect + 200ms poll loop. Under CI load (thread-pool cold starts, GC pauses,
    // noisy-neighbor runners) that connect-and-first-poll sequence can occasionally take longer than
    // the margin allowed, so the CancellationTokenSource fires early — mid-connect or mid-attach_start
    // — and the command exits 1 instead of 0 (confirmed in CI logs: "Expected exitCode to be 0, but
    // found 1"). Waiting on a TaskCompletionSource that the fake server's respond callback completes
    // once the calls under test have actually happened removes that race: cancellation now fires only
    // after the poll loop has demonstrably reached the state being asserted on, never before.
    //
    // Cancelling right on the TCS can land the token while the server is still sending the response
    // (the respond callback runs before the response bytes are written). That used to deadlock on
    // Windows: the client dropped its read and wrote attach_stop while the server was still blocked
    // writing the orphaned response, and neither side read (issue #321; an earlier hang dump showed
    // both ends in StreamWriter.FlushAsyncInternal). AttachPipeClient now finishes reading a response
    // that's already in flight before it honours cancellation, so these tests cancel immediately and
    // deliberately exercise that race. Each `await runTask` is also bounded, so a regression fails the
    // one test instead of hanging the run until VSTest's 5-minute blame timeout.

    [Fact]
    public async Task Attach_PrintsAttachStartsBacklog_BeforeAnyPolling()
    {
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serverCts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
        {
            object result = method switch
            {
                "attach_start" => new { kind = "terminal", text = "line1\nline2", total_lines = 2 },
                "attach_read"  => new { text = "", total_lines = 2 },
                "attach_stop"  => new { ok = true },
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            };
            if (method == "attach_read") firstRead.TrySetResult();
            return result;
        }, serverCts.Token);

        using var runCts = new CancellationTokenSource();
        var runTask = RunAttachAsync(pipeName, runCts.Token, "some-session");

        try
        {
            await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); // let at least one poll happen, then detach
        }
        finally { runCts.Cancel(); } // always cancel, even on timeout - otherwise the poll loop and fake server orphan forever

        var (exitCode, output, error) = await runTask.WaitAsync(RunTimeout, TestContext.Current.CancellationToken);
        serverCts.Cancel();

        exitCode.Should().Be(0);
        output.Should().Contain("line1\nline2");
        error.Should().Contain("Attached to some-session").And.Contain("Ctrl+C to detach");
        calls.Should().Contain(c => c.Method == "attach_start");
    }

    [Fact]
    public async Task Attach_PollLoop_TailsOnlyNewOutputSinceTheLastCursor_UntilCancelled()
    {
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        using var serverCts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, @params) =>
        {
            object result = method switch
            {
                "attach_start" => new { kind = "terminal", text = "", total_lines = 0 },
                "attach_read"  => @params.GetProperty("since").GetInt64() == 0
                    ? new { text = "alpha", total_lines = 5 }
                    : new { text = "", total_lines = 5 },
                "attach_stop" => new { ok = true },
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            };
            if (method == "attach_read" && Interlocked.Increment(ref readCount) == 2) secondRead.TrySetResult();
            return result;
        }, serverCts.Token);

        using var runCts = new CancellationTokenSource();
        var runTask = RunAttachAsync(pipeName, runCts.Token, "some-session");

        try
        {
            await secondRead.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); // wait for 2+ reads, then detach
        }
        finally { runCts.Cancel(); } // always cancel, even on timeout - otherwise the poll loop and fake server orphan forever

        var (exitCode, output, _) = await runTask.WaitAsync(RunTimeout, TestContext.Current.CancellationToken);
        serverCts.Cancel();

        exitCode.Should().Be(0);
        output.Should().Contain("alpha");

        var reads = calls.Where(c => c.Method == "attach_read").ToList();
        reads.Should().HaveCountGreaterThan(1);
        reads[0].Params.GetProperty("since").GetInt64().Should().Be(0);
        // Every read after the first must use the cursor from the prior response (5), never repeat 0 —
        // that's what makes this a tail, not a re-fetch of the same backlog every poll.
        reads.Skip(1).Should().OnlyContain(c => c.Params.GetProperty("since").GetInt64() == 5);
    }

    [Fact]
    public async Task Attach_CancellingLikeCtrlC_DetachesCleanly_CallingAttachStopExactlyOnce()
    {
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serverCts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
        {
            object result = method switch
            {
                "attach_start" => new { kind = "terminal", text = "", total_lines = 0 },
                "attach_read"  => new { text = "", total_lines = 0 },
                "attach_stop"  => new { ok = true },
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            };
            if (method == "attach_read") firstRead.TrySetResult();
            return result;
        }, serverCts.Token);

        using var runCts = new CancellationTokenSource();
        var runTask = RunAttachAsync(pipeName, runCts.Token, "some-session");

        try
        {
            await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally { runCts.Cancel(); } // always cancel, even on timeout - otherwise the poll loop and fake server orphan forever

        var (exitCode, _, _) = await runTask.WaitAsync(RunTimeout, TestContext.Current.CancellationToken);
        serverCts.Cancel();

        exitCode.Should().Be(0);
        calls.Count(c => c.Method == "attach_stop").Should().Be(1);
        calls[^1].Method.Should().Be("attach_stop", "the exclusive attach claim must be released last, after polling stops");
    }

    [Fact]
    public async Task AttachStartFailure_ReturnsExitCode1_WithACleanMessage_AndNeverCallsAttachStop()
    {
        using var serverCts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
            method switch
            {
                "attach_start" => throw new InvalidOperationException("Session 'some-session' is already attached from another client."),
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, serverCts.Token);

        var (exitCode, _, error) = await RunAttachAsync(pipeName, CancellationToken.None, "some-session");
        serverCts.Cancel();

        exitCode.Should().Be(1);
        error.Should().Contain("Cannot attach:").And.Contain("already attached from another client");
        calls.Should().NotContain(c => c.Method == "attach_stop", "a failed attach_start never claimed anything to release");
    }
}
