using AwesomeAssertions;
using PolyTerm.App;
using Xunit;

namespace PolyTerm.App.Tests;

public sealed class CrashLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"polyterm-crashlog-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WriteCrashReport_WritesTheExceptionTypeMessageAndStackToANewFile()
    {
        Exception thrown;
        try { throw new PlatformNotSupportedException("keychain says no"); }
        catch (Exception ex) { thrown = ex; }

        var path = CrashLog.WriteCrashReport(thrown, _dir, new DateTimeOffset(2026, 11, 21, 20, 7, 8, TimeSpan.FromHours(10)));

        path.Should().NotBeNull();
        Path.GetFileName(path).Should().Be("crash-20261121-200708-000.log");
        var text = File.ReadAllText(path!);
        text.Should().Contain("System.PlatformNotSupportedException: keychain says no");
        text.Should().Contain(nameof(WriteCrashReport_WritesTheExceptionTypeMessageAndStackToANewFile), "the stack trace should be included");
        text.Should().Contain("OS:");
    }

    [Fact]
    public void WriteCrashReport_KeepsOnlyTheNewestLogs()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < CrashLog.MaxCrashLogs + 5; i++)
            CrashLog.WriteCrashReport(new InvalidOperationException($"crash {i}"), _dir, start.AddMinutes(i));

        var remaining = Directory.GetFiles(_dir, "crash-*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        remaining.Should().HaveCount(CrashLog.MaxCrashLogs);
        remaining[0].Should().Be($"crash-{start.AddMinutes(5):yyyyMMdd-HHmmss-fff}.log", "the five oldest should have been pruned");
    }

    [Fact]
    public void WriteCrashReport_NeverThrows_WhenTheDirectoryCantBeCreated()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"polyterm-crashlog-file-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "a file where the log directory should go");
        try
        {
            CrashLog.WriteCrashReport(new InvalidOperationException("x"), blocker, DateTimeOffset.Now).Should().BeNull();
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void AppendUnobserved_AppendsToOneSharedFile()
    {
        CrashLog.AppendUnobserved(new InvalidOperationException("first"), _dir, DateTimeOffset.Now);
        CrashLog.AppendUnobserved(new InvalidOperationException("second"), _dir, DateTimeOffset.Now);

        Directory.GetFiles(_dir).Should().ContainSingle();
        var text = File.ReadAllText(Path.Combine(_dir, "unobserved-task-exceptions.log"));
        text.Should().Contain("first").And.Contain("second");
    }
}
