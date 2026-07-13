namespace PolyTerm.Protocols.Serial;

/// <summary>A COM port detected as currently present and USB-attached, ready to connect to.</summary>
public sealed record DetectedComPort(string PortName, string FriendlyName);

/// <summary>Detects USB-attached COM ports currently present on the local machine (issue #33).</summary>
public interface IComPortDetector
{
    /// <summary>
    /// Returns the USB-attached COM ports currently present, or an empty list if none are found or
    /// detection isn't supported on this platform. Best-effort: never throws — a detection failure
    /// (WMI unavailable, a transient query error) is treated the same as "no ports found" rather than
    /// surfacing an error for what's only a discovery convenience, not a required capability.
    /// </summary>
    IReadOnlyList<DetectedComPort> GetUsbPorts();
}
