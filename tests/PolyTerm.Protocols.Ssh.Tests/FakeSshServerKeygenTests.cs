using AwesomeAssertions;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>
/// <see cref="FakeSshServer"/> runs <c>ssh-keygen.exe</c> with its output redirected so it can't
/// write into the test host's stdout (issue #300). This pins down that the redirected stderr really
/// is captured into the failure message on Windows, rather than assuming it: a console-subsystem app
/// should write through the pipe, but a GUI-subsystem one wrote nothing that way (#282).
/// </summary>
public sealed class FakeSshServerKeygenTests
{
    [Fact]
    public async Task RunKeygen_WhenKeyCannotBeSaved_ThrowsWithSshKeygensStderr()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: FakeSshServer drives Windows OpenSSH's ssh-keygen.exe."); return; }

        var keyPath = Path.Combine(Path.GetTempPath(), $"polyterm-missing-dir-{Guid.NewGuid()}", "key");

        var act = () => FakeSshServer.RunKeygenAsync(keyPath, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().NotContain("stderr: (empty)",
            "ssh-keygen reports why it couldn't save the key on stderr, and the redirected pipe should capture it");
    }
}
