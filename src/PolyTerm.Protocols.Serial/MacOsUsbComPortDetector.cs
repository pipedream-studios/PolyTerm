using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Protocols.Serial;

/// <summary>
/// One serial port as IOKit reports it: its callout device path (<c>/dev/cu.*</c>) and, when the port
/// sits under a USB device in the IOService plane, that USB device's descriptor strings. A non-USB port
/// (Bluetooth, <c>debug-console</c>) has <see cref="IsUsb"/> false and no USB strings.
/// </summary>
internal sealed record MacSerialService(
    string CalloutDevice,
    bool IsUsb,
    string? UsbProductName,
    string? UsbVendorName);

/// <summary>
/// Detects USB-attached serial ports on macOS through IOKit (issue #91). It lists every
/// <c>IOSerialBSDClient</c> service and keeps the ones with a USB device (<c>IOUSBHostDevice</c>) among
/// their IOService-plane ancestors, so Bluetooth and built-in ports such as <c>cu.debug-console</c> are
/// left out. This is the same approach pyserial's macOS port listing takes, and it doesn't depend on a
/// driver's device-name pattern (<c>cu.usbserial-*</c>, <c>cu.usbmodem*</c>, <c>cu.SLAB_USBtoUART</c>,
/// <c>cu.wchusbserial*</c> all differ). The port reported is the callout device (<c>/dev/cu.*</c>), not
/// <c>/dev/tty.*</c>, because opening the tty device blocks until carrier detect is raised.
/// Verified against a Prolific PL2303 adapter behind a USB-C hub on Apple Silicon. See
/// <see cref="IComPortDetector.GetUsbPorts"/>'s doc comment for why this never throws.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsUsbComPortDetector : IComPortDetector
{
    private readonly ILogger<MacOsUsbComPortDetector> _logger;
    private readonly Func<IReadOnlyList<MacSerialService>> _enumerate;

    public MacOsUsbComPortDetector(ILogger<MacOsUsbComPortDetector> logger)
        : this(logger, MacOsIoKitSerialEnumerator.Enumerate) { }

    /// <summary>Test seam: replaces the live IOKit enumeration with a fixed list.</summary>
    internal MacOsUsbComPortDetector(ILogger<MacOsUsbComPortDetector> logger, Func<IReadOnlyList<MacSerialService>> enumerate)
    {
        _logger = logger;
        _enumerate = enumerate;
    }

    public IReadOnlyList<DetectedComPort> GetUsbPorts()
    {
        try
        {
            return _enumerate()
                .Where(s => s.IsUsb && !string.IsNullOrEmpty(s.CalloutDevice))
                .Select(s => new DetectedComPort(s.CalloutDevice, FormatFriendlyName(s)))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "USB COM port detection failed; treating as no ports found.");
            return [];
        }
    }

    /// <summary>
    /// The USB product string when the device reports one (e.g. "USB-Serial Controller D"), else the
    /// vendor string, else the device's own name (e.g. "usbserial-140"). The sidebar shows the port path
    /// alongside, so two identical adapters stay distinguishable.
    /// </summary>
    internal static string FormatFriendlyName(MacSerialService service)
    {
        if (!string.IsNullOrWhiteSpace(service.UsbProductName))
            return service.UsbProductName.Trim();
        if (!string.IsNullOrWhiteSpace(service.UsbVendorName))
            return service.UsbVendorName.Trim() + " USB serial";

        var name = Path.GetFileName(service.CalloutDevice);
        return name.StartsWith("cu.", StringComparison.Ordinal) ? name["cu.".Length..] : name;
    }
}
