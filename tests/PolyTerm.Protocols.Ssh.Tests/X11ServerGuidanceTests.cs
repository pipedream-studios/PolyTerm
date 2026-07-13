using AwesomeAssertions;
using PolyTerm.Protocols.Ssh.X11;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>Pure string-building logic — no OS/platform detection involved (that's the caller's job).</summary>
public sealed class X11ServerGuidanceTests
{
    [Fact]
    public void Windows_MentionsVcXsrv()
    {
        X11ServerGuidance.BuildUnreachableMessage(isWindows: true, isMacOs: false).Should().Contain("VcXsrv");
    }

    [Fact]
    public void MacOs_MentionsXQuartz()
    {
        X11ServerGuidance.BuildUnreachableMessage(isWindows: false, isMacOs: true).Should().Contain("XQuartz");
    }

    [Fact]
    public void NeitherWindowsNorMacOs_MentionsNeitherProduct_ButStillExplainsTheProblem()
    {
        var message = X11ServerGuidance.BuildUnreachableMessage(isWindows: false, isMacOs: false);
        message.Should().NotContain("VcXsrv").And.NotContain("XQuartz");
        message.Should().Contain("X server");
    }
}
