namespace PolyTerm.Protocols.Serial;

/// <summary>No-op detector for non-Windows platforms — <see cref="WmiUsbComPortDetector"/>'s WMI-based detection is Windows-only.</summary>
internal sealed class NullComPortDetector : IComPortDetector
{
    public IReadOnlyList<DetectedComPort> GetUsbPorts() => [];
}
