using System.IO.Pipes;
using System.Text;
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
public sealed class AttachCommandTerminalTests
{
    private sealed record Call(string Method, JsonElement Params);

    /// <summary>Starts a fake server that dispatches by method name via <paramref name="respond"/>, recording every call it receives.</summary>
    private static async Task<(string PipeName, List<Call> Calls)> StartFakeServerAsync(
        Func<string, JsonElement, object> respond, CancellationToken ct)
    {
        // Short prefix + truncated GUID (not the full 32 hex chars) - macOS CI runners now use a
        // $TMPDIR long enough that 'CoreFxPipe_polyterm-cli-...-<32 hex chars>' pushed the full Unix
        // domain socket path past .NET's 104-byte UnixDomainSocketEndPoint limit, failing with
        // ArgumentOutOfRangeException before the pipe ever connected - confirmed the hard way in CI.
        var pipeName = $"pt-{Guid.NewGuid():N}"[..15];
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var calls = new List<Call>();

        _ = Task.Run(async () =>
        {
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using var reader = new StreamReader(pipe, noBomUtf8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, noBomUtf8, leaveOpen: true) { AutoFlush = true };

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;

                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
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

                    await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* test teardown */ }
            finally { pipe.Dispose(); }
        }, ct);

        return (pipeName, calls);
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
    // That alone reintroduced a second, worse race: the respond callback fires *before* the server has
    // sent the response bytes back (see StartFakeServerAsync - the callback result is what gets
    // serialized and written next), so cancelling immediately on the TCS can land the CancellationToken
    // mid-transmission of that very response. Confirmed via a VSTest hang dump (--blame-hang-timeout):
    // both the fake server and the real client end up permanently blocked in
    // StreamWriter.FlushAsyncInternal at the same time - the client abandons its read mid-flight and
    // immediately issues a new write (attach_stop, in AttachToTerminalSessionAsync's finally block)
    // while the server is still trying to flush the orphaned response, deadlocking the pipe. The
    // original wall-clock version rarely hit this because its cancellation landed hundreds of
    // milliseconds after the read it was reacting to, not nanoseconds after. The short, fixed delay
    // below after each TCS wait restores that safety margin without reintroducing the original,
    // unbounded race: a local named-pipe round trip of a few dozen bytes completes in microseconds, so
    // it's a large multiple of the time actually needed, not a guess about how long setup will take.

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
            await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(30)); // let at least one poll happen, then detach
            await Task.Delay(250); // let that read's in-flight response finish being delivered before cancelling
        }
        finally { runCts.Cancel(); } // always cancel, even on timeout - otherwise the poll loop and fake server orphan forever

        var (exitCode, output, error) = await runTask;
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
            await secondRead.Task.WaitAsync(TimeSpan.FromSeconds(30)); // wait for 2+ reads, then detach
            await Task.Delay(250); // let that read's in-flight response finish being delivered before cancelling
        }
        finally { runCts.Cancel(); } // always cancel, even on timeout - otherwise the poll loop and fake server orphan forever

        var (exitCode, output, _) = await runTask;
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
            await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(250); // let that read's in-flight response finish being delivered before cancelling
        }
        finally { runCts.Cancel(); } // always cancel, even on timeout - otherwise the poll loop and fake server orphan forever

        var (exitCode, _, _) = await runTask;
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
