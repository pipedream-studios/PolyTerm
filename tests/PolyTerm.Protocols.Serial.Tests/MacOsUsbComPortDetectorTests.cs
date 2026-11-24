using System.Runtime.Versioning;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PolyTerm.Protocols.Serial.Tests;

/// <summary>
/// Coverage for <see cref="MacOsUsbComPortDetector"/> (issue #91). The filtering and naming tests feed
/// the detector a fixed IOKit listing through its internal seam; the live test calls real IOKit, which
/// on a CI runner (no adapter plugged in) checks that enumeration runs cleanly and returns no non-USB
/// ports, and on a Mac with an adapter also lists it.
/// </summary>
public sealed class MacOsUsbComPortDetectorTests
{
    // Same pattern as LinuxUsbComPortDetectorTests: a direct OperatingSystem guard in each test so the
    // platform-compat analyzer (CA1416) sees it, with a `return;` after Assert.Skip for the same reason.
    private const string SkipReason = "this test exercises MacOsUsbComPortDetector specifically - it only registers on macOS (see ServiceCollectionExtensions.AddPolyTermSerial).";

    [SupportedOSPlatform("macos")]
    private static MacOsUsbComPortDetector DetectorOver(params MacSerialService[] services)
        => new(NullLogger<MacOsUsbComPortDetector>.Instance, () => services);

    [Fact]
    public void GetUsbPorts_KeepsOnlyUsbPorts_AndReportsTheirCalloutDevice()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip(SkipReason); return; }

        var detector = DetectorOver(
            new MacSerialService("/dev/cu.debug-console", IsUsb: false, null, null),
            new MacSerialService("/dev/cu.Bluetooth-Incoming-Port", IsUsb: false, null, null),
            new MacSerialService("/dev/cu.usbserial-140", IsUsb: true, "USB-Serial Controller D", "Prolific Technology Inc."));

        var ports = detector.GetUsbPorts();

        ports.Should().ContainSingle();
        ports[0].PortName.Should().Be("/dev/cu.usbserial-140");
        ports[0].FriendlyName.Should().Be("USB-Serial Controller D");
    }

    [Fact]
    public void GetUsbPorts_WhenEnumerationThrows_ReturnsEmpty()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip(SkipReason); return; }

        var detector = new MacOsUsbComPortDetector(
            NullLogger<MacOsUsbComPortDetector>.Instance,
            () => throw new DllNotFoundException("IOKit"));

        detector.GetUsbPorts().Should().BeEmpty("detection is a discovery convenience and is documented never to throw");
    }

    [Theory]
    [InlineData("USB-Serial Controller D", "Prolific Technology Inc.", "/dev/cu.usbserial-140", "USB-Serial Controller D")]
    [InlineData(null, "FTDI", "/dev/cu.usbserial-A50285BI", "FTDI USB serial")]
    [InlineData("  ", null, "/dev/cu.usbmodem14101", "usbmodem14101")]
    public void FormatFriendlyName_PrefersProduct_ThenVendor_ThenDeviceName(string? product, string? vendor, string callout, string expected)
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip(SkipReason); return; }

        MacOsUsbComPortDetector.FormatFriendlyName(new MacSerialService(callout, IsUsb: true, product, vendor))
            .Should().Be(expected);
    }

    [Fact]
    public void GetUsbPorts_AgainstRealIoKit_ReturnsOnlyExistingUsbCalloutDevices()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip(SkipReason); return; }

        var all = MacOsIoKitSerialEnumerator.Enumerate();
        var ports = new MacOsUsbComPortDetector(NullLogger<MacOsUsbComPortDetector>.Instance).GetUsbPorts();

        all.Should().OnlyContain(s => s.CalloutDevice.StartsWith("/dev/cu.", StringComparison.Ordinal));
        all.Where(s => !s.IsUsb).Should().OnlyContain(s => s.UsbProductName == null && s.UsbVendorName == null);
        ports.Should().OnlyContain(p => File.Exists(p.PortName), "every reported port should be a real device node");
        ports.Select(p => p.PortName).Should().NotContain(
            p => p.Contains("Bluetooth", StringComparison.Ordinal) || p.Contains("debug-console", StringComparison.Ordinal),
            "built-in and Bluetooth serial ports have no USB device ancestor");
    }
}
