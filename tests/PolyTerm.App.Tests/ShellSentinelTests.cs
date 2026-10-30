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
    [InlineData(ShellKind.PowerShell, "Get-Item x;", "Get-Item x; \"__PT_ab12cd34_$(if($?){0}elseif($LASTEXITCODE){$LASTEXITCODE}else{1})__\"")]
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

        ShellSentinel.CleanOutput(lines, 2, "", wrapped.Suffix, Token).Should().Equal("$ uname", "Linux");
    }

    [Fact]
    public void CleanOutput_AlsoCutsPartialCopiesOfTheSuffix_LikePowerShellRedrawsAndErrorQuotes()
    {
        var wrapped = ShellSentinel.Wrap("Get-Item C:\\nope", ShellKind.PowerShell, Token);
        string[] lines =
        [
            "> Get-Item C:\\nope; \"__PT_ab12cd34_$(if($?){0}elseif($LAS",   // redraw cut off at the terminal width
            "Get-Item : Cannot find path 'C:\\nope' because it does not exist.",
            "+ Get-Item C:\\nope; \"__PT_ab12cd34_$(if($?){0}elseif($LASTEXITCODE ...",
            "__PT_ab12cd34_1__",
        ];

        ShellSentinel.CleanOutput(lines, 3, "", wrapped.Suffix, Token).Should().Equal(
            "> Get-Item C:\\nope",
            "Get-Item : Cannot find path 'C:\\nope' because it does not exist.",
            "+ Get-Item C:\\nope");
    }

    [Theory]
    [InlineData("powershell -NoLogo", true)]
    [InlineData("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", true)]
    [InlineData("pwsh -Command Get-Date", false)]
    [InlineData("bash", true)]
    [InlineData("bash --login", true)]
    [InlineData("bash build.sh", false)]
    [InlineData("bash -c 'echo hi'", false)]
    [InlineData("cmd", true)]
    [InlineData("cmd /c exit 5", false)]
    [InlineData("  ssh rob@destiny", true)]
    [InlineData("ssh -p 2222 -i key rob@destiny", true)]
    [InlineData("ssh rob@destiny uptime", false)]
    [InlineData("ssh -q destiny uptime", false)]
    [InlineData("sudo -i", true)]
    [InlineData("sudo su", true)]
    [InlineData("sudo bash", true)]
    [InlineData("sudo apt update", false)]
    [InlineData("su - postgres", true)]
    [InlineData("docker exec -it web sh", true)]
    [InlineData("docker exec web ls", false)]
    [InlineData("exit", true)]
    [InlineData("ls -la", false)]
    [InlineData("bashful --help", false)]
    public void SwitchesShell_SpotsCommandsThatStartOrLeaveAnInteractiveShell(string command, bool expected)
    {
        ShellSentinel.SwitchesShell(command).Should().Be(expected);
    }
}
