using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using PolyTerm.Protocols.LocalShell;
using Xunit;

namespace PolyTerm.Protocols.LocalShell.Tests;

/// <summary>
/// Real, live coverage of the ConPTY-backed local shell session - closes out
/// https://github.com/chris3081/PolyTerm/issues/23 (reported as "zero terminal output") and
/// https://github.com/chris3081/PolyTerm/issues/2 (reported as an intermittent
/// STATUS_DLL_INIT_FAILED crash starting the shell). Both were the same bug: ConPtyTerminal's
/// call to UpdateProcThreadAttribute passed the pseudoconsole handle via `ref` to a local variable
/// instead of by value, matching Microsoft's own C sample's `UpdateProcThreadAttribute(..., hpc,
/// sizeof(hpc), ...)` (hpc passed directly, not &amp;hpc). PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE's
/// lpValue is read again later, when CreateProcess itself processes the attribute list - so the
/// `ref` version left the attribute list holding the address of a stack-local variable rather than
/// the handle value itself. Whether that address still held the right bits by the time CreateProcess
/// read it depended entirely on what the JIT did with that stack slot/register in between, which is
/// exactly why the crash was intermittent, and why it went from "usually fine" to "always crashes"
/// once <c>StartProcessInPseudoConsole</c> was pulled out into its own method (a smaller, separately
/// JITted method with different register allocation). The hosted process crashed at 0ms with
/// STATUS_DLL_INIT_FAILED (0xC0000142) every time; there was never any real "silent ReadLoop
/// failure" as issue #23 originally hypothesized - the shell simply never survived long enough to
/// produce anything to read.
/// </summary>
public sealed class LocalShellSessionTests
{
    private sealed class UnusedHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            throw new InvalidOperationException("Local shell sessions never evaluate a host key policy.");
    }

    [Fact]
    public async Task ConnectAsync_StartsARealShellProcess_AndProducesRealOutputWithoutCrashing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: this test exercises ConPtyTerminal specifically (cmd.exe) - see UnixPtyTerminalTests for the Linux/macOS equivalent.");
            return;
        }

        var profile = new ConnectionProfile
        {
            Name = "local-shell-test",
            ProtocolId = "local-shell",
            Settings = new Dictionary<string, JsonElement>
            {
                ["shell"] = JsonSerializer.SerializeToElement("cmd.exe"),
            },
        };
        var ctx = new SessionContext
        {
            SessionId = Guid.NewGuid(),
            Profile = profile,
            HostKeyPolicy = new UnusedHostKeyPolicy(),
        };

        await using var session = new LocalShellSession(ctx, NullLogger<LocalShellSession>.Instance);
        await session.ConnectAsync();

        session.State.Should().Be(SessionState.Connected);
        session.Terminal.Should().NotBeNull();

        var gotOutput = new TaskCompletionSource();
        session.Terminal!.OutputReceived += (_, data) =>
        {
            if (data.Length > 0)
                gotOutput.TrySetResult();
        };

        // Before the fix, the hosted cmd.exe crashed with STATUS_DLL_INIT_FAILED (0xC0000142) at
        // 0ms every time, so this would time out waiting forever for output that would never come.
        var completed = await Task.WhenAny(gotOutput.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().Be(gotOutput.Task, "the shell process should stay alive and emit real terminal output instead of crashing instantly");
    }
}
