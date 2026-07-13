using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Protocols.Serial;

/// <summary>
/// Detects USB-attached COM ports via WMI's <c>Win32_PnPEntity</c> class, filtered to the "Ports"
/// PnP class and a <c>PNPDeviceID</c> containing "USB" — the same heuristic used for the current
/// device instance path of a genuinely USB-attached serial adapter (CDC-ACM, FTDI, Silicon Labs
/// CP210x, Prolific, etc.), as opposed to a motherboard's built-in COM1 (typically enumerated under
/// <c>ACPI\...</c>, with no "USB" in its device path at all). Deliberately a substring check, not an
/// exact prefix match — some USB-to-serial bus drivers rename the enumerator prefix on the device
/// instance path (e.g. FTDI's own bus enumerator), but "USB" still reliably appears somewhere in a
/// genuinely USB-attached device's full <c>PNPDeviceID</c>. This is a pragmatic, documented
/// simplification (see issue #33) rather than a guarantee against every possible edge case; it isn't
/// verified against real hardware in this environment (none was available to test against), so it
/// deliberately never throws — see <see cref="IComPortDetector.GetUsbPorts"/>'s own doc comment.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WmiUsbComPortDetector : IComPortDetector
{
    private readonly ILogger<WmiUsbComPortDetector> _logger;

    public WmiUsbComPortDetector(ILogger<WmiUsbComPortDetector> logger) => _logger = logger;

    public IReadOnlyList<DetectedComPort> GetUsbPorts()
    {
        var results = new List<DetectedComPort>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");
            using var found = searcher.Get();

            foreach (var obj in found)
            {
                using (obj)
                {
                    var name = obj["Name"] as string;
                    var pnpDeviceId = obj["PNPDeviceID"] as string;
                    if (name is null || pnpDeviceId is null) continue;
                    if (!pnpDeviceId.Contains("USB", StringComparison.OrdinalIgnoreCase)) continue;

                    var match = ComPortNameRegex().Match(name);
                    if (!match.Success) continue;

                    var portName = match.Value[1..^1]; // strip the surrounding "(" ")"
                    results.Add(new DetectedComPort(portName, name));
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

    [GeneratedRegex(@"\(COM\d+\)")]
    private static partial Regex ComPortNameRegex();
}
