using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.Serial.Tests;

/// <summary>
/// A serial session whose port goes away under it must leave Connected (issue #91: unplugging a USB
/// adapter left the tab showing a live session). A pseudo-terminal stands in for the adapter: the
/// session opens the pty's secondary side as its port. Unplugging an adapter on macOS fails the
/// pending read with ENXIO (checked with a PL2303); <c>revoke(2)</c> on the secondary has the same
/// effect, while closing the primary side does not (the read just stays pending). Linux has no
/// <c>revoke</c>, so the port-loss test is macOS only; Windows has no pty a SerialPort can open.
/// </summary>
public sealed class SerialSessionPortLossTests
{
    private const string SkipReason = "needs a Unix pseudo-terminal to stand in for a serial adapter.";
    private const string RevokeSkipReason = "needs revoke(2) on a pseudo-terminal to stand in for an unplugged adapter; Linux has no revoke.";

    private sealed class UnusedHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            throw new InvalidOperationException("Serial sessions never evaluate a host key policy.");
    }

    [Fact]
    public async Task PortLost_WhileConnected_FaultsTheSession_AndReportsAFatalError()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip(RevokeSkipReason); return; }

        var primary = Pty.Open(out var secondaryPath);
        var session = NewSession(secondaryPath);
        try
        {
            var errors = new List<SessionErrorEventArgs>();
            session.ErrorOccurred += (_, e) => { lock (errors) errors.Add(e); };
            var output = new StringBuilder();
            await session.ConnectAsync(TestContext.Current.CancellationToken);
            session.Terminal!.OutputReceived += (_, data) => { lock (output) output.Append(Encoding.ASCII.GetString(data.Span)); };

            // Data flows first, so the failure below is the port going away and not a port that never worked.
            Pty.Write(primary, "hello");
            (await WaitForAsync(() => { lock (output) return output.ToString().Contains("hello", StringComparison.Ordinal); }))
                .Should().BeTrue("bytes written to the pty's primary side should reach the session");

            Pty.Revoke(secondaryPath);

            (await WaitForAsync(() => session.State == SessionState.Faulted))
                .Should().BeTrue($"losing the port should fault the session, but it stayed {session.State}");
            lock (errors)
            {
                errors.Should().ContainSingle();
                errors[0].IsFatal.Should().BeTrue();
                errors[0].Exception.Message.Should().Contain(secondaryPath);
            }
        }
        finally
        {
            Pty.Close(primary);
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disconnect_ByTheUser_ClosesTheSession_WithoutAnError()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip(SkipReason); return; }

        var primary = Pty.Open(out var secondaryPath);
        var session = NewSession(secondaryPath);
        try
        {
            var errors = 0;
            session.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);
            await session.ConnectAsync(TestContext.Current.CancellationToken);
            session.Terminal!.OutputReceived += (_, _) => { };

            await session.DisconnectAsync(TestContext.Current.CancellationToken);
            await session.DisconnectAsync(TestContext.Current.CancellationToken);

            session.State.Should().Be(SessionState.Closed);
            await Task.Delay(200, TestContext.Current.CancellationToken); // let the cancelled read loop finish
            Volatile.Read(ref errors).Should().Be(0, "closing the port ourselves is not a failure");
        }
        finally
        {
            Pty.Close(primary);
            await session.DisposeAsync();
        }
    }

    private static SerialSession NewSession(string portName)
    {
        var profile = new ConnectionProfile
        {
            Name = "serial-pty-test", ProtocolId = "serial",
            Settings = new Dictionary<string, JsonElement> { ["portName"] = JsonSerializer.SerializeToElement(portName) },
        };
        var ctx = new SessionContext { SessionId = Guid.NewGuid(), Profile = profile, HostKeyPolicy = new UnusedHostKeyPolicy() };
        return new SerialSession(ctx, NullLogger<SerialSession>.Instance);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>Minimal libc pty calls: open a primary, unlock its secondary and return its path.</summary>
    private static class Pty
    {
        private const int ORdWr = 0x2;

        public static int Open(out string secondaryPath)
        {
            var noCtty = OperatingSystem.IsMacOS() ? 0x20000 : 0x100;
            var fd = posix_openpt(ORdWr | noCtty);
            if (fd < 0) throw new InvalidOperationException($"posix_openpt failed: errno {Marshal.GetLastPInvokeError()}");
            if (grantpt(fd) != 0 || unlockpt(fd) != 0)
                throw new InvalidOperationException($"grantpt/unlockpt failed: errno {Marshal.GetLastPInvokeError()}");
            secondaryPath = Marshal.PtrToStringUTF8(ptsname(fd))
                ?? throw new InvalidOperationException("ptsname returned null.");
            return fd;
        }

        public static void Write(int fd, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            if (write(fd, bytes, bytes.Length) != bytes.Length)
                throw new InvalidOperationException($"write failed: errno {Marshal.GetLastPInvokeError()}");
        }

        public static void Close(int fd) => _ = close(fd);

        /// <summary>Invalidates every open handle to the device, as macOS does to a USB serial port's handles when the adapter is unplugged.</summary>
        public static void Revoke(string path)
        {
            if (revoke(Encoding.UTF8.GetBytes(path + "\0")) != 0)
                throw new InvalidOperationException($"revoke failed: errno {Marshal.GetLastPInvokeError()}");
        }

        [DllImport("libc", SetLastError = true)] private static extern int revoke(byte[] path);

        [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
        [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
        [DllImport("libc", SetLastError = true)] private static extern IntPtr ptsname(int fd);
        [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] buf, nint count);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    }
}
