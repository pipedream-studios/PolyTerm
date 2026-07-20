using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Protocols.Serial;

/// <summary>
/// Detects USB-attached COM ports on Linux by enumerating <c>/dev/serial/by-id/*</c> — udev's own
/// stable, vendor/product-named symlinks for USB serial adapters (CDC-ACM, FTDI, Silicon Labs CP210x,
/// Prolific, etc.), each pointing at the adapter's real device node (e.g. <c>/dev/ttyUSB0</c>,
/// <c>/dev/ttyACM0</c>). Deliberately reads this directory rather than walking <c>/sys/class/tty/*</c>
/// or binding against libudev directly — <c>/dev/serial/by-id</c> already does exactly the
/// USB-vs-built-in filtering <see cref="WmiUsbComPortDetector"/> does via a WMI query on Windows (a
/// motherboard's built-in serial port, if any, is never a member of this directory, and neither is a
/// non-USB device), and udev maintains it on every mainstream distro without any extra runtime
/// dependency or P/Invoke binding. Not yet verified against real USB-serial hardware (issue #71) - see
/// <see cref="IComPortDetector.GetUsbPorts"/>'s own doc comment for why this deliberately never throws.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxUsbComPortDetector : IComPortDetector
{
    private const string DefaultByIdDirectory = "/dev/serial/by-id";

    private readonly ILogger<LinuxUsbComPortDetector> _logger;
    private readonly string _byIdDirectory;

    public LinuxUsbComPortDetector(ILogger<LinuxUsbComPortDetector> logger) : this(logger, DefaultByIdDirectory) { }

    /// <summary>Test-only seam: points detection at a fake by-id directory instead of the real <c>/dev/serial/by-id</c>, which nothing but root can populate.</summary>
    internal LinuxUsbComPortDetector(ILogger<LinuxUsbComPortDetector> logger, string byIdDirectory)
    {
        _logger = logger;
        _byIdDirectory = byIdDirectory;
    }

    public IReadOnlyList<DetectedComPort> GetUsbPorts()
    {
        var results = new List<DetectedComPort>();
        try
        {
            // Nothing to enumerate - and not an error - on a machine with no USB serial adapter ever
            // plugged in: udev only creates this directory the first time it has something to put in it.
            if (!Directory.Exists(_byIdDirectory))
                return [];

            foreach (var linkPath in Directory.EnumerateFileSystemEntries(_byIdDirectory))
            {
                try
                {
                    // ResolveLinkTarget(..., returnFinalTarget: true) follows the by-id symlink all the
                    // way to the real device node (e.g. "/dev/ttyUSB0") - that's what SerialPort needs
                    // to open a connection, not the by-id path itself.
                    var target = File.ResolveLinkTarget(linkPath, returnFinalTarget: true);
                    if (target is null) continue;

                    results.Add(new DetectedComPort(target.FullName, FormatFriendlyName(Path.GetFileName(linkPath))));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to resolve serial-by-id entry {Entry}; skipping it.", linkPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "USB COM port detection failed; treating as no ports found.");
            return [];
        }
        return results;
    }

    /// <summary>
    /// udev's by-id names are underscore-joined and usually carry a trailing <c>-ifNN-portN</c> USB
    /// interface suffix (e.g. <c>usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0</c>) - strips the
    /// leading "usb-" and the interface suffix, and turns underscores into spaces, so the sidebar shows
    /// something closer to "FTDI FT232R USB UART A50285BI" than the raw symlink name.
    /// </summary>
    private static string FormatFriendlyName(string byIdName)
    {
        var name = byIdName.StartsWith("usb-", StringComparison.Ordinal) ? byIdName["usb-".Length..] : byIdName;
        var ifSuffixIndex = name.IndexOf("-if", StringComparison.Ordinal);
        if (ifSuffixIndex >= 0) name = name[..ifSuffixIndex];
        return name.Replace('_', ' ');
    }
}
