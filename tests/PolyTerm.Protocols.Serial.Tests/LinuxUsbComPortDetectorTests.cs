using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PolyTerm.Protocols.Serial.Tests;

/// <summary>
/// Real, live coverage of <see cref="LinuxUsbComPortDetector"/> (issue #71) against a real temp
/// directory of real symlinks - not a WMI-style hard-to-fake API, so unlike
/// <c>WmiUsbComPortDetector</c> (still untested, see its own doc comment) this one has no excuse not
/// to be exercised end-to-end. Uses the internal (<c>InternalsVisibleTo</c>-gated) constructor
/// overload to point detection at a throwaway directory instead of the real
/// <c>/dev/serial/by-id</c>, which nothing but root can populate with a genuine USB adapter plugged
/// in. Skips on non-Linux - this exercises the Linux-specific implementation, not a
/// platform-abstraction seam.
/// </summary>
public sealed class LinuxUsbComPortDetectorTests : IDisposable
{
    private readonly string _byIdDir = Path.Combine(Path.GetTempPath(), "polyterm-serial-by-id-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_byIdDir))
            Directory.Delete(_byIdDir, recursive: true);
    }

    // Not a bool-returning helper: the platform-compat analyzer (CA1416) only recognizes a direct
    // `if (!OperatingSystem.IsLinux())` guard in the same method as the call it's protecting - routing
    // it through a shared helper makes the analyzer unable to see the guard, even though it's
    // functionally identical at runtime.
    private const string SkipReason = "this test exercises LinuxUsbComPortDetector specifically - it only registers on Linux (see ServiceCollectionExtensions.AddPolyTermSerial).";

    [Fact]
    public void GetUsbPorts_WhenByIdDirectoryDoesNotExist_ReturnsEmptyWithoutThrowing()
    {
        if (!OperatingSystem.IsLinux()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        var detector = new LinuxUsbComPortDetector(NullLogger<LinuxUsbComPortDetector>.Instance, _byIdDir);

        detector.GetUsbPorts().Should().BeEmpty("udev only creates /dev/serial/by-id once a USB serial adapter has actually been plugged in - a machine that's never had one shouldn't error, just report no ports");
    }

    [Fact]
    public void GetUsbPorts_ResolvesARealSymlink_ToItsRealDeviceNodeAndAFriendlyName()
    {
        if (!OperatingSystem.IsLinux()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        Directory.CreateDirectory(_byIdDir);
        var fakeDeviceNode = Path.Combine(Path.GetTempPath(), "polyterm-fake-ttyUSB0-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(fakeDeviceNode, ""); // stands in for a real /dev/ttyUSBn char device node
        try
        {
            var linkPath = Path.Combine(_byIdDir, "usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0");
            File.CreateSymbolicLink(linkPath, fakeDeviceNode);

            var detector = new LinuxUsbComPortDetector(NullLogger<LinuxUsbComPortDetector>.Instance, _byIdDir);
            var ports = detector.GetUsbPorts();

            ports.Should().HaveCount(1);
            ports[0].PortName.Should().Be(fakeDeviceNode, "GetUsbPorts should resolve the by-id symlink to the real device node SerialPort can actually open, not the by-id path itself");
            ports[0].FriendlyName.Should().Be("FTDI FT232R USB UART A50285BI", "the leading 'usb-' prefix and trailing '-ifNN-portN' USB interface suffix should be stripped, and underscores turned into spaces");
        }
        finally
        {
            File.Delete(fakeDeviceNode);
        }
    }

    [Fact]
    public void GetUsbPorts_WithMultipleAdapters_ReturnsOneEntryPerRealSymlink()
    {
        if (!OperatingSystem.IsLinux()) { Console.WriteLine($"SKIP: {SkipReason}"); return; }

        Directory.CreateDirectory(_byIdDir);
        var deviceA = Path.Combine(Path.GetTempPath(), "polyterm-fake-ttyUSB0-" + Guid.NewGuid().ToString("N"));
        var deviceB = Path.Combine(Path.GetTempPath(), "polyterm-fake-ttyACM0-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(deviceA, "");
        File.WriteAllText(deviceB, "");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_byIdDir, "usb-FTDI_FT232R-if00-port0"), deviceA);
            File.CreateSymbolicLink(Path.Combine(_byIdDir, "usb-Silicon_Labs_CP2102-if00-port0"), deviceB);

            var detector = new LinuxUsbComPortDetector(NullLogger<LinuxUsbComPortDetector>.Instance, _byIdDir);
            var ports = detector.GetUsbPorts();

            ports.Should().HaveCount(2);
            ports.Select(p => p.PortName).Should().BeEquivalentTo([deviceA, deviceB]);
        }
        finally
        {
            File.Delete(deviceA);
            File.Delete(deviceB);
        }
    }
}
