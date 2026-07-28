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
        var pipeName = $"polyterm-cli-termtest-{Guid.NewGuid():N}";
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

    [Fact]
    public async Task Attach_PrintsAttachStartsBacklog_BeforeAnyPolling()
    {
        using var serverCts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
            method switch
            {
                "attach_start" => new { kind = "terminal", text = "line1\nline2", total_lines = 2 },
                "attach_read"  => new { text = "", total_lines = 2 },
                "attach_stop"  => new { ok = true },
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, serverCts.Token);

        using var runCts = new CancellationTokenSource();
        runCts.CancelAfter(TimeSpan.FromMilliseconds(350)); // let at least one poll happen, then detach

        var (exitCode, output, error) = await RunAttachAsync(pipeName, runCts.Token, "some-session");
        serverCts.Cancel();

        exitCode.Should().Be(0);
        output.Should().Contain("line1\nline2");
        error.Should().Contain("Attached to some-session").And.Contain("Ctrl+C to detach");
        calls.Should().Contain(c => c.Method == "attach_start");
    }

    [Fact]
    public async Task Attach_PollLoop_TailsOnlyNewOutputSinceTheLastCursor_UntilCancelled()
    {
        using var serverCts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, @params) =>
            method switch
            {
                "attach_start" => new { kind = "terminal", text = "", total_lines = 0 },
                "attach_read"  => @params.GetProperty("since").GetInt64() == 0
                    ? new { text = "alpha", total_lines = 5 }
                    : new { text = "", total_lines = 5 },
                "attach_stop" => new { ok = true },
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, serverCts.Token);

        using var runCts = new CancellationTokenSource();
        runCts.CancelAfter(TimeSpan.FromMilliseconds(550)); // 200ms poll interval — enough for 2+ reads

        var (exitCode, output, _) = await RunAttachAsync(pipeName, runCts.Token, "some-session");
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
        using var serverCts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
            method switch
            {
                "attach_start" => new { kind = "terminal", text = "", total_lines = 0 },
                "attach_read"  => new { text = "", total_lines = 0 },
                "attach_stop"  => new { ok = true },
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, serverCts.Token);

        using var runCts = new CancellationTokenSource();
        runCts.CancelAfter(TimeSpan.FromMilliseconds(350));

        var (exitCode, _, _) = await RunAttachAsync(pipeName, runCts.Token, "some-session");
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
