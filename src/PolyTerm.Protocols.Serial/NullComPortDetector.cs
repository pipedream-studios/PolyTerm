namespace PolyTerm.Protocols.Serial;

/// <summary>No-op detector for macOS (issue #91) — <see cref="WmiUsbComPortDetector"/>'s WMI-based detection is Windows-only, and <see cref="LinuxUsbComPortDetector"/>'s <c>/dev/serial/by-id</c>-based detection is Linux-only.</summary>
internal sealed class NullComPortDetector : IComPortDetector
{
    public IReadOnlyList<DetectedComPort> GetUsbPorts() => [];
}
