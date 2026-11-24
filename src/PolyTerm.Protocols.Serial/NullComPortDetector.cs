namespace PolyTerm.Protocols.Serial;

/// <summary>No-op detector for any OS other than Windows, Linux and macOS, which have <see cref="WmiUsbComPortDetector"/>, <see cref="LinuxUsbComPortDetector"/> and <see cref="MacOsUsbComPortDetector"/>.</summary>
internal sealed class NullComPortDetector : IComPortDetector
{
    public IReadOnlyList<DetectedComPort> GetUsbPorts() => [];
}
