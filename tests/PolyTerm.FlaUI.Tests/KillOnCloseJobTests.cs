using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Proves <see cref="KillOnCloseJob"/> kills processes started *after* assignment, not just the one
/// assigned — the case that matters for <see cref="PolyTermAppFixture"/>, where WebView2 and the
/// external browser are started by PolyTerm.exe long after launch. No app launch needed: a plain
/// cmd.exe parent stands in for PolyTerm.exe and a PowerShell child for the orphaned browser.
/// </summary>
public sealed class KillOnCloseJobTests
{
    [Fact]
    public void Dispose_KillsProcessesStartedByTheAssignedProcess()
    {
        // cmd.exe with redirected stdin sits waiting for input, so it's assigned before it starts anything.
        using var parent = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;

        var job = new KillOnCloseJob();
        Process? child = null;
        try
        {
            job.Assign(parent.Id);

            // The child prints its own PID so the test can find it, then outlives the test unless killed.
            parent.StandardInput.WriteLine("powershell -NoProfile -Command \"'CHILDPID=' + $PID; Start-Sleep 120\"");
            string? line;
            while ((line = parent.StandardOutput.ReadLine()) is not null && !line.StartsWith("CHILDPID=", StringComparison.Ordinal)) { }
            line.Should().NotBeNull("the child process should have reported its PID");
            child = Process.GetProcessById(int.Parse(line!["CHILDPID=".Length..], System.Globalization.CultureInfo.InvariantCulture));

            job.Dispose();

            child.WaitForExit(10_000).Should().BeTrue("the job should kill the grandchild, not just the assigned process");
            parent.WaitForExit(10_000).Should().BeTrue("the job should kill the assigned process itself");
        }
        finally
        {
            job.Dispose();
            try { child?.Kill(); } catch { /* already gone */ }
            try { parent.Kill(entireProcessTree: true); } catch { /* already gone */ }
            child?.Dispose();
        }
    }
}
