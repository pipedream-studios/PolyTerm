using System.IO.Ports;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace PolyTerm.Protocols.Serial.Tests;

/// <summary>How a profile's settings configure the port (issue #342). The port is never opened, so no hardware is needed.</summary>
public sealed class SerialSessionPortSetupTests
{
    [Theory]
    [InlineData("None", Handshake.None)]
    [InlineData("XOnXOff", Handshake.XOnXOff)]
    [InlineData("RequestToSend", Handshake.RequestToSend)]
    [InlineData("RequestToSendXOnXOff", Handshake.RequestToSendXOnXOff)]
    [InlineData("requesttosend", Handshake.RequestToSend)]
    [InlineData("sideways", Handshake.None)]
    public void CreatePort_UsesTheProfilesFlowControl(string flowControl, Handshake expected)
    {
        using var port = SerialSession.CreatePort(Settings(("flowControl", flowControl)));

        port.Handshake.Should().Be(expected);
    }

    [Fact]
    public void CreatePort_WithOnlyAPortName_DefaultsTo9600_8N1_WithoutFlowControl()
    {
        using var port = SerialSession.CreatePort(Settings());

        port.PortName.Should().Be("COM9");
        port.BaudRate.Should().Be(9600);
        port.DataBits.Should().Be(8);
        port.Parity.Should().Be(Parity.None);
        port.StopBits.Should().Be(StopBits.One);
        port.Handshake.Should().Be(Handshake.None);
        port.IsOpen.Should().BeFalse();
    }

    private static Dictionary<string, JsonElement> Settings(params (string Key, string Value)[] extra) =>
        extra.Append(("portName", "COM9"))
            .ToDictionary(s => s.Item1, s => JsonSerializer.SerializeToElement(s.Item2));
}
