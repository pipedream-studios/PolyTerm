using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>Covers issue #281: the app's minimum log level and its <c>POLYTERM_LOG_LEVEL</c> override.</summary>
public sealed class AppLogLevelTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NoOverride_ReturnsDefault(string? value)
    {
        var level = AppLogLevel.Resolve(value, LogLevel.Information, out var invalid);

        level.Should().Be(LogLevel.Information);
        invalid.Should().BeFalse();
    }

    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData(" Error ", LogLevel.Error)]
    [InlineData("critical", LogLevel.Critical)]
    [InlineData("None", LogLevel.None)]
    public void Resolve_LevelName_ReturnsThatLevel(string value, LogLevel expected)
    {
        var level = AppLogLevel.Resolve(value, LogLevel.Information, out var invalid);

        level.Should().Be(expected);
        invalid.Should().BeFalse();
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("3")]
    [InlineData("42")]
    [InlineData("Debug,Trace")]
    public void Resolve_NotALevelName_ReturnsDefaultAndFlagsInvalid(string value)
    {
        var level = AppLogLevel.Resolve(value, LogLevel.Information, out var invalid);

        level.Should().Be(LogLevel.Information);
        invalid.Should().BeTrue();
    }

    [Fact]
    public void BuildDefault_MatchesBuildConfiguration()
    {
#if DEBUG
        AppLogLevel.BuildDefault.Should().Be(LogLevel.Debug);
#else
        AppLogLevel.BuildDefault.Should().Be(LogLevel.Information);
#endif
    }
}
