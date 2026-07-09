using FluentAssertions;
using PolyTerm.App.McpBridge;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="McpRateLimiter"/>'s sliding-window logic (spec §4.7 design rule 4) directly —
/// the pipe-level "does an over-limit call actually get rejected instead of served" behavior is
/// covered separately in <c>McpBridgeServicePipeTests</c> against a real connection.
/// </summary>
public sealed class McpRateLimiterTests
{
    [Fact]
    public void TryAcquire_AllowsCalls_UpToTheLimit()
    {
        var limiter = new McpRateLimiter();

        limiter.TryAcquire(3).Should().BeTrue();
        limiter.TryAcquire(3).Should().BeTrue();
        limiter.TryAcquire(3).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_RejectsTheCallThatWouldExceedTheLimit()
    {
        var limiter = new McpRateLimiter();
        limiter.TryAcquire(2).Should().BeTrue();
        limiter.TryAcquire(2).Should().BeTrue();

        limiter.TryAcquire(2).Should().BeFalse("a third call within the same window exceeds a limit of 2");
    }

    [Fact]
    public void TryAcquire_IsUnlimited_WhenLimitIsZeroOrNegative()
    {
        var limiter = new McpRateLimiter();

        for (var i = 0; i < 50; i++)
            limiter.TryAcquire(0).Should().BeTrue();

        var limiter2 = new McpRateLimiter();
        limiter2.TryAcquire(-1).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_TracksEachInstanceIndependently()
    {
        // Mirrors the real usage: one McpRateLimiter per pipe connection, so one busy client's
        // budget is entirely separate from another's.
        var limiterA = new McpRateLimiter();
        var limiterB = new McpRateLimiter();

        limiterA.TryAcquire(1).Should().BeTrue();
        limiterA.TryAcquire(1).Should().BeFalse();

        limiterB.TryAcquire(1).Should().BeTrue("limiter B has its own independent budget");
    }
}
