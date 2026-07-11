using System.IO.Ports;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.Serial.Tests;

/// <summary>
/// Real, live coverage of the serial protocol's connect/read/write happy path against an actual Cisco
/// IOS switch console wired to COM3 on this developer's machine via a USB-to-serial adapter - closes out
/// https://github.com/chris3081/PolyTerm/issues/6. Skips gracefully wherever COM3 isn't a real, present
/// device (every other machine, including CI - see PolyTerm.Portable.slnx's own comment on why this test
/// project is excluded there), or wherever <c>POLYTERM_TEST_SERIAL_HOSTNAME</c> isn't set - the switch's
/// real hostname is only ever configured in this developer's own local environment, never committed.
/// </summary>
public sealed class SerialSessionTests
{
    private const string PortName = "COM3";

    private static readonly string? HostnamePrompt = Environment.GetEnvironmentVariable("POLYTERM_TEST_SERIAL_HOSTNAME") is { Length: > 0 } h
        ? $"{h}>" : null;

    /// <summary>Serial has no handshake to verify identity, so any policy satisfies <see cref="SessionContext"/>'s required property - never actually called for this protocol.</summary>
    private sealed class UnusedHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            throw new InvalidOperationException("Serial sessions never evaluate a host key policy.");
    }

    [Fact]
    public async Task ConnectAsync_OpensARealPort_AndReadWriteRoundTripsWithARealCiscoConsole()
    {
        if (!SerialPort.GetPortNames().Contains(PortName, StringComparer.OrdinalIgnoreCase) || HostnamePrompt is null)
        {
            Console.WriteLine($"SKIP: {PortName} not present on this machine's port list ({string.Join(", ", SerialPort.GetPortNames())}), or POLYTERM_TEST_SERIAL_HOSTNAME not set.");
            return;
        }

        var profile = new ConnectionProfile
        {
            Name = "serial-real-device-test", ProtocolId = "serial",
            Settings = new Dictionary<string, JsonElement>
            {
                ["portName"] = JsonSerializer.SerializeToElement(PortName),
                ["baudRate"] = JsonSerializer.SerializeToElement(9600),
                ["dataBits"] = JsonSerializer.SerializeToElement(8),
                ["parity"]   = JsonSerializer.SerializeToElement("None"),
                ["stopBits"] = JsonSerializer.SerializeToElement("One"),
            },
        };
        var ctx = new SessionContext
        {
            SessionId = Guid.NewGuid(), Profile = profile, HostKeyPolicy = new UnusedHostKeyPolicy(),
        };
        var session = new SerialSession(ctx, NullLogger<SerialSession>.Instance);

        try
        {
            await session.ConnectAsync();
            session.State.Should().Be(SessionState.Connected);
            session.Terminal.Should().NotBeNull("a connected serial session should expose a terminal channel");

            var outputBuf = new StringBuilder();
            var lockObj = new object();
            session.Terminal!.OutputReceived += (_, data) =>
            {
                lock (lockObj) outputBuf.Append(Encoding.ASCII.GetString(data.Span));
            };

            // Wake the console and confirm the real switch echoes its own prompt back over the wire.
            await session.Terminal.SendInputAsync(Encoding.ASCII.GetBytes("\r\n"));
            var sawPrompt = await WaitForAsync(() => Contains(outputBuf, lockObj, HostnamePrompt), TimeSpan.FromSeconds(5));
            sawPrompt.Should().BeTrue($"the real Cisco switch should echo its '{HostnamePrompt}' prompt back after a real write over the wire");

            // A real, distinctive, read-only command - confirms genuine two-way data flow, not just an
            // echoed newline. "show version" is safe (no config change) and its banner text is specific
            // enough that a false positive (some other device/buffered garbage) is implausible.
            lock (lockObj) outputBuf.Clear();
            await session.Terminal.SendInputAsync(Encoding.ASCII.GetBytes("show version\r\n"));
            var sawVersion = await WaitForAsync(() => Contains(outputBuf, lockObj, "Cisco IOS Software"), TimeSpan.FromSeconds(5));
            sawVersion.Should().BeTrue("a real 'show version' round-trip should return the real switch's own IOS banner text");

            string finalOutput;
            lock (lockObj) finalOutput = outputBuf.ToString();
            finalOutput.Should().Contain("C3560E", "the real switch's specific model should appear in its own 'show version' output");
        }
        finally
        {
            await session.DisconnectAsync();
            await session.DisposeAsync();
        }
    }

    private static bool Contains(StringBuilder buf, object lockObj, string needle)
    {
        lock (lockObj) return buf.ToString().Contains(needle, StringComparison.Ordinal);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }
}
