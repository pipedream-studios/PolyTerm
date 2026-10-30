using AwesomeAssertions;
using PolyTerm.App.McpBridge;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>Covers <see cref="ShellSentinel"/>: identifying a session's shell, and the per-shell completion markers.</summary>
public sealed class ShellSentinelTests
{
    private const string Token = "ab12cd34";

    [Theory]
    [InlineData("__PT_SH_ab12cd34_/", ShellKind.Posix)]
    [InlineData("__PT_SH_ab12cd34_3.7.1/", ShellKind.Fish)]
    [InlineData("__PT_SH_ab12cd34_/System.Collections.Hashtable", ShellKind.PowerShell)]
    [InlineData("\"__PT_SH_ab12cd34_$FISH_VERSION/$PSVersionTable\"", ShellKind.Cmd)]
    public void ParseProbe_TellsTheShellsApartByWhatTheProbePrinted(string output, ShellKind expected)
    {
        string[] lines = [$"rob@destiny:~$ {ShellSentinel.ProbeCommand(Token)}", output];

        ShellSentinel.ParseProbe(lines, Token).Should().Be(expected);
    }

    [Fact]
    public void ParseProbe_IgnoresTheEchoedProbe_AndAnotherProbesOutput()
    {
        ShellSentinel.ParseProbe([$"C:\\Users\\rob>{ShellSentinel.ProbeCommand(Token)}"], Token).Should().BeNull(
            "the echoed command line (prompt first) isn't the output - cmd's output is the same text without the prompt");
        ShellSentinel.ParseProbe(["__PT_SH_ffffffff_/"], Token).Should().BeNull();
    }

    [Theory]
    [InlineData(ShellKind.Posix, "make test", "make test; echo \"__PT_ab12cd34_$?__\"")]
    [InlineData(ShellKind.Posix, "ls;  ", "ls; echo \"__PT_ab12cd34_$?__\"")]
    [InlineData(ShellKind.Posix, "sleep 5 &", "sleep 5 & echo \"__PT_ab12cd34_$?__\"")]
    [InlineData(ShellKind.Posix, "true && false", "true && false; echo \"__PT_ab12cd34_$?__\"")]
    [InlineData(ShellKind.Fish, "make test", "make test; echo \"__PT_ab12cd34_\"$status\"__\"")]
    [InlineData(ShellKind.PowerShell, "Get-Item x;", "Get-Item x; \"__PT_ab12cd34_$(if ($?) { 0 } elseif ($LASTEXITCODE) { $LASTEXITCODE } else { 1 })__\"")]
    [InlineData(ShellKind.Cmd, "dir", "dir & call echo __PT_ab12cd34_%^ERRORLEVEL%__")]
    [InlineData(ShellKind.Unknown, "show version", "show version")]
    public void Wrap_AppendsTheShellsMarker(ShellKind kind, string command, string expected)
    {
        var wrapped = ShellSentinel.Wrap(command, kind, Token);

        wrapped.Text.Should().Be(expected);
        wrapped.Text.Should().EndWith(wrapped.Suffix);
    }

    [Theory]
    [InlineData(ShellKind.Posix)]
    [InlineData(ShellKind.Fish)]
    [InlineData(ShellKind.PowerShell)]
    [InlineData(ShellKind.Cmd)]
    public void TryFindMarker_DoesNotMatchTheEchoedCommandLine(ShellKind kind)
    {
        var echoed = "prompt> " + ShellSentinel.Wrap("exit 3", kind, Token).Text;

        ShellSentinel.TryFindMarker([echoed], Token, out _, out _, out _).Should().BeFalse(
            "the echo holds the marker's source ($?, %^ERRORLEVEL%...), not an exit code");
    }

    [Fact]
    public void TryFindMarker_ReadsTheExitCode_AndOutputAheadOfItOnTheSameLine()
    {
        string[] lines = ["$ printf done", "done__PT_ab12cd34_127__"];

        ShellSentinel.TryFindMarker(lines, Token, out var code, out var line, out var before).Should().BeTrue();
        code.Should().Be(127);
        line.Should().Be(1);
        before.Should().Be("done");
    }

    [Fact]
    public void CleanOutput_DropsTheMarker_AndTheSuffixFromTheEchoedCommand()
    {
        var wrapped = ShellSentinel.Wrap("uname", ShellKind.Posix, Token);
        string[] lines = ["$ " + wrapped.Text, "Linux", "__PT_ab12cd34_0__", "stray"];

        ShellSentinel.CleanOutput(lines, 2, "", wrapped.Suffix).Should().Equal("$ uname", "Linux");
    }

    [Theory]
    [InlineData("bash", true)]
    [InlineData("  ssh rob@destiny", true)]
    [InlineData("sudo -i", true)]
    [InlineData("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoLogo", true)]
    [InlineData("exit", true)]
    [InlineData("ls -la", false)]
    [InlineData("bashful --help", false)]
    public void MayChangeShell_SpotsCommandsThatStartLeaveOrSwitchShells(string command, bool expected)
    {
        ShellSentinel.MayChangeShell(command).Should().Be(expected);
    }
}
