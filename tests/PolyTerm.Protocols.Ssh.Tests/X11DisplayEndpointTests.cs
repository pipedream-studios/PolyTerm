using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using PolyTerm.Protocols.Ssh.X11;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>Pure DISPLAY-string parsing logic — no SSH/network involved, unlike the rest of this project's tests.</summary>
public sealed class X11DisplayEndpointTests
{
    [Fact]
    public void LinuxLocalDisplay_ResolvesToUnixSocket()
    {
        var result = X11DisplayEndpoint.Resolve(":0", isWindows: false);

        result.Endpoint.Should().BeOfType<UnixDomainSocketEndPoint>();
        ((UnixDomainSocketEndPoint)result.Endpoint).ToString().Should().Contain("/tmp/.X11-unix/X0");
        result.DisplayNumber.Should().Be(0);
        result.ScreenNumber.Should().Be(0);
    }

    [Fact]
    public void LinuxDisplayWithScreen_ParsesScreenNumber()
    {
        var result = X11DisplayEndpoint.Resolve(":10.2", isWindows: false);

        result.DisplayNumber.Should().Be(10);
        result.ScreenNumber.Should().Be(2);
    }

    [Fact]
    public void ExplicitUnixPrefix_ResolvesToUnixSocketEvenWithAHostLikeSubstring()
    {
        var result = X11DisplayEndpoint.Resolve("unix:0", isWindows: false);

        result.Endpoint.Should().BeOfType<UnixDomainSocketEndPoint>();
    }

    [Fact]
    public void RemoteHostDisplay_ResolvesToTcpEndpointOnPort6000PlusDisplay()
    {
        var result = X11DisplayEndpoint.Resolve("192.168.1.5:3", isWindows: false);

        result.Endpoint.Should().BeOfType<IPEndPoint>();
        var ep = (IPEndPoint)result.Endpoint;
        ep.Address.Should().Be(IPAddress.Parse("192.168.1.5"));
        ep.Port.Should().Be(6003);
    }

    [Fact]
    public void WindowsWithNoDisplayHostSpecified_ResolvesToLoopbackTcp_NotUnixSocket()
    {
        // Windows has no Unix-socket X11 convention — an empty host must fall back to TCP even
        // though the same ":0" string means "Unix socket" on Linux/macOS.
        var result = X11DisplayEndpoint.Resolve(":0", isWindows: true);

        result.Endpoint.Should().BeOfType<IPEndPoint>();
        var ep = (IPEndPoint)result.Endpoint;
        ep.Address.Should().Be(IPAddress.Loopback);
        ep.Port.Should().Be(6000);
    }

    [Fact]
    public void MissingDisplayVariable_UsesPlatformDefault()
    {
        X11DisplayEndpoint.Resolve(null, isWindows: true).Endpoint.Should().BeOfType<IPEndPoint>();
        X11DisplayEndpoint.Resolve("", isWindows: false).Endpoint.Should().BeOfType<UnixDomainSocketEndPoint>();
    }

    [Fact]
    public void MalformedDisplay_ThrowsFormatException()
    {
        var act = () => X11DisplayEndpoint.Resolve("not-a-display-string", isWindows: false);
        act.Should().Throw<FormatException>();
    }
}
