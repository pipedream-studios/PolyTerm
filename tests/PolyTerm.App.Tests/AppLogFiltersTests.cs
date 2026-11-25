using AwesomeAssertions;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Incoming;
using MarcusW.VncClient.Protocol.Implementation.Services.Communication;
using MarcusW.VncClient.Protocol.Implementation.Services.Handshaking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>Covers issue #343: which VNC library log categories get through at each log level.</summary>
public sealed class AppLogFiltersTests
{
    // Real type names rather than strings, so a library upgrade that moves one fails here instead of
    // silently switching the filter off.
    private static readonly string MessageSender = typeof(RfbMessageSender).FullName!;
    private static readonly string FramebufferUpdates = typeof(FramebufferUpdateMessageType).FullName!;
    private static readonly string Handshaker = typeof(RfbHandshaker).FullName!;
    private static readonly string Connection = typeof(RfbConnection).FullName!;
    private static readonly string OwnCategory = typeof(AppLogFilters).FullName!;

    [Fact]
    public void Debug_HoldsTheWholeVncLibraryAtInformation()
    {
        using var logging = new Logging(LogLevel.Debug, includeVncKeystrokes: false);

        foreach (var category in new[] { MessageSender, FramebufferUpdates, Handshaker, Connection })
        {
            logging.Enabled(category, LogLevel.Debug).Should().BeFalse(category);
            logging.Enabled(category, LogLevel.Information).Should().BeTrue(category);
        }

        logging.Enabled(OwnCategory, LogLevel.Debug).Should().BeTrue();
    }

    [Fact]
    public void Trace_ShowsTheVncLibraryExceptItsMessageSender()
    {
        using var logging = new Logging(LogLevel.Trace, includeVncKeystrokes: false);

        logging.Enabled(FramebufferUpdates, LogLevel.Trace).Should().BeTrue();
        logging.Enabled(Handshaker, LogLevel.Debug).Should().BeTrue();
        logging.Enabled(MessageSender, LogLevel.Debug).Should().BeFalse();
        logging.Enabled(MessageSender, LogLevel.Information).Should().BeTrue();
        logging.Enabled(OwnCategory, LogLevel.Trace).Should().BeTrue();
    }

    [Fact]
    public void TraceWithKeystrokeOptIn_FiltersNothing()
    {
        using var logging = new Logging(LogLevel.Trace, includeVncKeystrokes: true);

        logging.Enabled(MessageSender, LogLevel.Trace).Should().BeTrue();
        logging.Enabled(FramebufferUpdates, LogLevel.Trace).Should().BeTrue();
    }

    [Fact]
    public void DebugWithKeystrokeOptIn_StillHidesTheMessageSender()
    {
        using var logging = new Logging(LogLevel.Debug, includeVncKeystrokes: true);

        logging.Enabled(MessageSender, LogLevel.Debug).Should().BeFalse();
        logging.Enabled(FramebufferUpdates, LogLevel.Debug).Should().BeFalse();
    }

    [Theory]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    [InlineData(LogLevel.None)]
    public void InformationAndAbove_VncLibraryFollowsTheChosenLevel(LogLevel minimum)
    {
        using var logging = new Logging(minimum, includeVncKeystrokes: false);

        foreach (var category in new[] { MessageSender, FramebufferUpdates, Connection })
        {
            foreach (var level in new[] { LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical })
                logging.Enabled(category, level).Should().Be(logging.Enabled(OwnCategory, level), "{0} at {1}", category, level);
        }
    }

    [Theory]
    [InlineData(LogLevel.Trace, true, true)]
    [InlineData(LogLevel.Trace, false, false)]
    [InlineData(LogLevel.Debug, true, false)]
    [InlineData(LogLevel.Information, true, false)]
    public void IncludesVncKeystrokes_NeedsTraceAndTheOptIn(LogLevel level, bool optedIn, bool expected) =>
        AppLogFilters.IncludesVncKeystrokes(level, optedIn).Should().Be(expected);

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    public void IsOptedIn_OnlyAcceptsOneOrTrue(string? value, bool expected) =>
        AppLogFilters.IsOptedIn(value).Should().Be(expected);

    /// <summary>A real logger factory configured the way the app's composition root configures it.</summary>
    private sealed class Logging : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ILoggerFactory _factory;

        public Logging(LogLevel level, bool includeVncKeystrokes)
        {
            var services = new ServiceCollection();
            // A factory with no provider reports every level as disabled. AddDebug won't do either:
            // its logger is only enabled while a debugger is attached.
            services.AddLogging(b => AppLogFilters.Apply(b.AddProvider(new AcceptAllProvider()), level, includeVncKeystrokes));
            _provider = services.BuildServiceProvider();
            _factory = _provider.GetRequiredService<ILoggerFactory>();
        }

        public bool Enabled(string category, LogLevel level) => _factory.CreateLogger(category).IsEnabled(level);

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>A provider that accepts everything, leaving the factory's filter rules to decide.</summary>
    private sealed class AcceptAllProvider : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }

        public void Dispose()
        {
        }
    }
}
