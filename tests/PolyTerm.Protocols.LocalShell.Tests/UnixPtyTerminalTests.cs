using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using PolyTerm.Protocols.LocalShell;
using Xunit;

namespace PolyTerm.Protocols.LocalShell.Tests;

/// <summary>
/// Real, live coverage of the Linux/macOS local shell session, backed by <c>UnixPtyTerminal</c>'s
/// real <c>forkpty()</c> implementation (issue #69) - closes out the "zero automated test coverage of
/// this path, on either platform" gap that issue's own description flagged, replacing the earlier M0
/// <c>script(1)</c>-wrapping placeholder that had none. Goes through the public
/// <see cref="LocalShellSession"/>/<see cref="ITerminalChannel"/> API throughout, the same way
/// <c>LocalShellSessionTests</c> (the Windows/ConPTY equivalent) does - no <c>InternalsVisibleTo</c>
/// needed, <c>UnixPtyTerminal</c> itself stays internal.
/// </summary>
public sealed class UnixPtyTerminalTests
{
    private sealed class UnusedHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            throw new InvalidOperationException("Local shell sessions never evaluate a host key policy.");
    }

    private static bool ShouldSkip(out string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            reason = "this test exercises UnixPtyTerminal specifically - see LocalShellSessionTests for the Windows/ConPTY equivalent.";
            return true;
        }
        reason = "";
        return false;
    }

    private static async Task<LocalShellSession> ConnectAsync()
    {
        var profile = new ConnectionProfile
        {
            Name = "unix-pty-test",
            ProtocolId = "local-shell",
            Settings = new Dictionary<string, JsonElement>
            {
                // /bin/sh (not $SHELL) - the one shell guaranteed to exist and behave predictably
                // (POSIX `stty size` output shape) across every real Linux/macOS box, unlike bash/zsh/etc.
                ["shell"] = JsonSerializer.SerializeToElement("/bin/sh"),
            },
        };
        var ctx = new SessionContext
        {
            SessionId = Guid.NewGuid(),
            Profile = profile,
            HostKeyPolicy = new UnusedHostKeyPolicy(),
        };

        var session = new LocalShellSession(ctx, NullLogger<LocalShellSession>.Instance);
        await session.ConnectAsync();
        return session;
    }

    /// <summary>Accumulates OutputReceived bytes as text and waits until <paramref name="contains"/> shows up, or times out.</summary>
    private static async Task<string> WaitForOutputContainingAsync(ITerminalChannel terminal, string contains, TimeSpan timeout)
    {
        var buffer = new StringBuilder();
        var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOutput(object? _, ReadOnlyMemory<byte> data)
        {
            lock (buffer)
            {
                buffer.Append(Encoding.UTF8.GetString(data.Span));
                if (buffer.ToString().Contains(contains, StringComparison.Ordinal))
                    found.TrySetResult();
            }
        }
        terminal.OutputReceived += OnOutput;
        try
        {
            var completed = await Task.WhenAny(found.Task, Task.Delay(timeout));
            completed.Should().Be(found.Task, $"terminal output should have contained '{contains}' within {timeout} - actual output so far:\n{buffer}");
            lock (buffer) return buffer.ToString();
        }
        finally
        {
            terminal.OutputReceived -= OnOutput;
        }
    }

    [Fact]
    public async Task ConnectAsync_StartsARealShellProcess_AndProducesRealOutputWithoutCrashing()
    {
        if (ShouldSkip(out var reason)) { Console.WriteLine($"SKIP: {reason}"); return; }

        await using var session = await ConnectAsync();

        session.State.Should().Be(SessionState.Connected);
        session.Terminal.Should().NotBeNull();

        var gotOutput = new TaskCompletionSource();
        session.Terminal!.OutputReceived += (_, data) =>
        {
            if (data.Length > 0)
                gotOutput.TrySetResult();
        };

        var completed = await Task.WhenAny(gotOutput.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        completed.Should().Be(gotOutput.Task, "the forked shell should stay alive and emit real PTY output (a prompt, at minimum) instead of exiting or crashing instantly");
    }

    [Fact]
    public async Task SendInputAsync_RoundTripsARealCommand_ThroughTheForkedShell()
    {
        if (ShouldSkip(out var reason)) { Console.WriteLine($"SKIP: {reason}"); return; }

        await using var session = await ConnectAsync();
        var terminal = session.Terminal!;

        // A distinctive marker (not just "hello") so this can't false-positive match shell startup
        // noise (motd, prompt text, etc.) that happens to already be sitting in the PTY buffer.
        const string marker = "polyterm-unix-pty-roundtrip-8f3a1c";
        await terminal.SendInputAsync(Encoding.UTF8.GetBytes($"echo {marker}\n"));

        var output = await WaitForOutputContainingAsync(terminal, marker, TimeSpan.FromSeconds(20));
        output.Should().Contain(marker, "the shell should have echoed back what was actually typed into it, proving Input reaches the forked process and Output reads its real response");
    }

    [Fact]
    public async Task ResizeAsync_ActuallyReachesTheKernelPtyRecord_UnlikeTheOldNoOpPlaceholder()
    {
        if (ShouldSkip(out var reason)) { Console.WriteLine($"SKIP: {reason}"); return; }

        await using var session = await ConnectAsync();
        var terminal = session.Terminal!;

        // LocalShellSession.CreateUnixPtyGuarded hardcodes the initial size to 80x24 - `stty size`
        // reports "ROWS COLS", so the initial query should show 24 80.
        await terminal.SendInputAsync(Encoding.UTF8.GetBytes("stty size\n"));
        var before = await WaitForOutputContainingAsync(terminal, "24 80", TimeSpan.FromSeconds(20));
        before.Should().Contain("24 80", "the PTY should start at LocalShellSession's hardcoded initial 80x24 size");

        await terminal.ResizeAsync(100, 40);

        // ioctl(TIOCSWINSZ) delivers SIGWINCH to the shell's foreground process group asynchronously -
        // give the shell a moment to actually handle it before typing the next command, rather than
        // racing it. (This test's real macOS failure, chased down via a real CI diagnostic, turned out
        // to be a different bug entirely - ioctl()'s real variadic signature being unreliable to call
        // directly via P/Invoke on Apple Silicon, fixed in UnixPtyTerminal.Resize() itself via
        // native/PolyTerm-pty-shim; see that method's own doc comment. This delay and the generous
        // TimeSpan.FromSeconds(20) timeouts below - bumped from 10s after this test also flaked once
        // on a loaded Ubuntu CI runner with a near-empty buffer - are separate, ordinary CI headroom.)
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        // Before the real ioctl(TIOCSWINSZ) implementation, Resize() was a documented no-op (issue
        // #69) - this specific assertion is exactly what would have failed against that placeholder,
        // since the shell would still report the original 24 80 forever.
        await terminal.SendInputAsync(Encoding.UTF8.GetBytes("stty size\n"));
        var after = await WaitForOutputContainingAsync(terminal, "40 100", TimeSpan.FromSeconds(20));
        after.Should().Contain("40 100", "ResizeAsync should reach the kernel's real PTY winsize record via ioctl(TIOCSWINSZ), not silently do nothing");
    }
}
