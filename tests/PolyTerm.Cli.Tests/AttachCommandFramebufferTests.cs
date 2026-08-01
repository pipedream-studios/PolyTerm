using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using PolyTerm.Cli.Commands;
using Xunit;

namespace PolyTerm.Cli.Tests;

/// <summary>
/// Covers <c>polyterm attach</c>'s framebuffer (RDP/VNC) branch (issue #119) — the CLI-facing follow-up
/// to #115's server-side-only plumbing. A terminal can't render a live desktop, so this command offers
/// one-shot <c>--click</c>/<c>--key</c>/<c>--type</c>/<c>--resize</c> flags plus <c>--snapshot-dir</c>
/// polling instead of a live view; those all terminate on their own (unlike the terminal path's
/// continuous poll loop, which — like <c>ConnectCommandTests</c>' documented gap for the connect-success
/// path — isn't covered here since it would hang an automated test process). Runs against a small
/// hand-rolled fake pipe server, same approach as <c>AttachPipeClientTests</c>, just keyed by method
/// name since a full attach sequence issues several different calls in order.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class AttachCommandFramebufferTests
{
    private static readonly JsonElement s_ok = JsonSerializer.SerializeToElement(new { ok = true });

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

                    var result = respond(method, @params);
                    var response = JsonSerializer.Serialize(new { id, result });
                    await writer.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* test teardown */ }
            finally { pipe.Dispose(); }
        }, ct);

        return (pipeName, calls);
    }

    private static object FramebufferAttachStartResult(long version = 1) => new
    {
        kind = "framebuffer",
        image_base64 = Convert.ToBase64String([1, 2, 3, 4]),
        width = 800,
        height = 600,
        format = "Bgra32",
        version,
    };

    private static async Task<(int ExitCode, string Out, string Error)> RunAttachAsync(string pipeName, params string[] args)
    {
        var previous = Environment.GetEnvironmentVariable("POLYTERM_PIPE");
        Environment.SetEnvironmentVariable("POLYTERM_PIPE", pipeName);
        try
        {
            var command = AttachCommand.Build();
            return await ConsoleCapture.RunAsync(command, args);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POLYTERM_PIPE", previous);
        }
    }

    [Fact]
    public async Task Click_SendsPressThenReleaseAtTheGivenCoordinatesAndButton_ThenDetaches()
    {
        using var cts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
            method switch
            {
                "attach_start" => FramebufferAttachStartResult(),
                "attach_framebuffer_send_pointer" => s_ok,
                "attach_stop" => s_ok,
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, cts.Token);

        var (exitCode, _, error) = await RunAttachAsync(pipeName, "some-session", "--click", "10,20", "--button", "right");

        exitCode.Should().Be(0);
        error.Should().Contain("Clicked (right) at (10, 20)");

        var pointerCalls = calls.Where(c => c.Method == "attach_framebuffer_send_pointer").ToList();
        pointerCalls.Should().HaveCount(2);
        pointerCalls[0].Params.GetProperty("kind").GetString().Should().Be("press");
        pointerCalls[1].Params.GetProperty("kind").GetString().Should().Be("release");
        foreach (var call in pointerCalls)
        {
            call.Params.GetProperty("x").GetInt32().Should().Be(10);
            call.Params.GetProperty("y").GetInt32().Should().Be(20);
            call.Params.GetProperty("button").GetString().Should().Be("right");
        }
        calls.Select(c => c.Method).Should().ContainInOrder("attach_start", "attach_framebuffer_send_pointer", "attach_framebuffer_send_pointer", "attach_stop");
    }

    [Fact]
    public async Task Resize_SendsTheParsedWidthAndHeight()
    {
        using var cts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
            method switch
            {
                "attach_start" => FramebufferAttachStartResult(),
                "attach_framebuffer_resize" => s_ok,
                "attach_stop" => s_ok,
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, cts.Token);

        var (exitCode, _, error) = await RunAttachAsync(pipeName, "some-session", "--resize", "800x600");

        exitCode.Should().Be(0);
        error.Should().Contain("Resized to 800x600");
        var resize = calls.Single(c => c.Method == "attach_framebuffer_resize");
        resize.Params.GetProperty("width").GetInt32().Should().Be(800);
        resize.Params.GetProperty("height").GetInt32().Should().Be(600);
    }

    [Fact]
    public async Task Key_SendsTheNamedKeysymAsPressThenRelease()
    {
        using var cts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
            method switch
            {
                "attach_start" => FramebufferAttachStartResult(),
                "attach_framebuffer_send_key" => s_ok,
                "attach_stop" => s_ok,
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, cts.Token);

        var (exitCode, _, error) = await RunAttachAsync(pipeName, "some-session", "--key", "Return");

        exitCode.Should().Be(0);
        error.Should().Contain("Pressed Return");
        var keyCalls = calls.Where(c => c.Method == "attach_framebuffer_send_key").ToList();
        keyCalls.Should().HaveCount(2);
        keyCalls[0].Params.GetProperty("keysym").GetString().Should().Be("Return");
        keyCalls[0].Params.GetProperty("kind").GetString().Should().Be("press");
        keyCalls[1].Params.GetProperty("keysym").GetString().Should().Be("Return");
        keyCalls[1].Params.GetProperty("kind").GetString().Should().Be("release");
    }

    [Fact]
    public async Task Type_SendsOneNumericKeysymPressReleasePairPerCharacter_MatchingFramebufferTypeAsyncsConvention()
    {
        using var cts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
            method switch
            {
                "attach_start" => FramebufferAttachStartResult(),
                "attach_framebuffer_send_key" => s_ok,
                "attach_stop" => s_ok,
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, cts.Token);

        var (exitCode, _, error) = await RunAttachAsync(pipeName, "some-session", "--type", "Ab");

        exitCode.Should().Be(0);
        error.Should().Contain("Typed 2 character(s)");

        var keyCalls = calls.Where(c => c.Method == "attach_framebuffer_send_key").ToList();
        keyCalls.Should().HaveCount(4);
        // 'A' = U+0041 (65), 'b' = U+0062 (98) — Latin-1 range maps directly to the code point, same
        // conversion FramebufferTypeAsync uses server-side.
        keyCalls[0].Params.GetProperty("keysym").GetString().Should().Be("65");
        keyCalls[0].Params.GetProperty("kind").GetString().Should().Be("press");
        keyCalls[1].Params.GetProperty("keysym").GetString().Should().Be("65");
        keyCalls[1].Params.GetProperty("kind").GetString().Should().Be("release");
        keyCalls[2].Params.GetProperty("keysym").GetString().Should().Be("98");
        keyCalls[3].Params.GetProperty("keysym").GetString().Should().Be("98");
    }

    [Fact]
    public async Task InvalidClickFormat_ReturnsExitCode1_AndNeverCallsTheServerForIt()
    {
        using var cts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
            method switch
            {
                "attach_start" => FramebufferAttachStartResult(),
                "attach_stop" => s_ok,
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, cts.Token);

        var (exitCode, _, error) = await RunAttachAsync(pipeName, "some-session", "--click", "not-a-point");

        exitCode.Should().Be(1);
        error.Should().Contain("--click expects");
        calls.Should().NotContain(c => c.Method == "attach_framebuffer_send_pointer");
    }

    [Fact]
    public async Task InvalidResizeFormat_ReturnsExitCode1()
    {
        using var cts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
            method switch
            {
                "attach_start" => FramebufferAttachStartResult(),
                "attach_stop" => s_ok,
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, cts.Token);

        var (exitCode, _, error) = await RunAttachAsync(pipeName, "some-session", "--resize", "bogus");

        exitCode.Should().Be(1);
        error.Should().Contain("--resize expects");
        calls.Should().NotContain(c => c.Method == "attach_framebuffer_resize");
    }

    [Fact]
    public async Task NoFlagsAndNoSnapshotDir_ExplainsATerminalCantRenderADesktop_AndReturnsExitCode1()
    {
        using var cts = new CancellationTokenSource();
        var (pipeName, calls) = await StartFakeServerAsync((method, _) =>
            method switch
            {
                "attach_start" => FramebufferAttachStartResult(),
                "attach_stop" => s_ok,
                _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
            }, cts.Token);

        var (exitCode, _, error) = await RunAttachAsync(pipeName, "some-session");

        exitCode.Should().Be(1);
        error.Should().Contain("a terminal can't render it live");
        error.Should().Contain("--snapshot-dir");
        calls.Select(c => c.Method).Should().Contain("attach_stop", "the exclusive attach claim must still be released even when nothing else happens");
    }

    [Fact]
    public async Task OneShotActionWithSnapshotDir_SavesAFreshFrameAfterTheActionAndReturnsExitCode0()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"polyterm-attach-test-{Guid.NewGuid():N}");
        try
        {
            using var cts = new CancellationTokenSource();
            var (pipeName, calls) = await StartFakeServerAsync((method, @params) =>
                method switch
                {
                    "attach_start" => FramebufferAttachStartResult(version: 1),
                    "attach_framebuffer_send_key" => s_ok,
                    "attach_framebuffer_read" => new { kind = "framebuffer", image_base64 = Convert.ToBase64String([9, 9, 9]), width = 800, height = 600, format = "Bgra32", version = 2L },
                    "attach_stop" => s_ok,
                    _ => throw new InvalidOperationException($"Unexpected method '{method}'"),
                }, cts.Token);

            var (exitCode, _, error) = await RunAttachAsync(pipeName, "some-session", "--key", "Return", "--snapshot-dir", tempDir);

            exitCode.Should().Be(0);
            error.Should().Contain("Saved 800x600 frame v2");

            var savedFile = Path.Combine(tempDir, "frame-00000002.png");
            File.Exists(savedFile).Should().BeTrue();
            File.ReadAllBytes(savedFile).Should().Equal([9, 9, 9]);

            // The fresh-frame fetch must come after the action, not before, and use since_version: 0
            // to force a real frame back rather than a `changed:false` no-op.
            var readCall = calls.Single(c => c.Method == "attach_framebuffer_read");
            readCall.Params.GetProperty("since_version").GetInt64().Should().Be(0);
            calls.Select(c => c.Method).Should().ContainInOrder("attach_start", "attach_framebuffer_send_key", "attach_framebuffer_send_key", "attach_framebuffer_read", "attach_stop");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
