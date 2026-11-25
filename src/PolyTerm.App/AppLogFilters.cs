using Microsoft.Extensions.Logging;

namespace PolyTerm.App;

/// <summary>
/// The app's log filtering on top of <see cref="AppLogLevel"/> (issue #343). The VNC library
/// (MarcusW.VncClient) logs several lines per screen update at <see cref="LogLevel.Debug"/>, and its
/// message sender logs the parameters of every outgoing message, including the key symbol of every
/// keystroke, so anything typed into a VNC session (passwords too) can be read back from the log.
/// <list type="bullet">
/// <item><see cref="LogLevel.Debug"/>: the whole library is held at Information.</item>
/// <item><see cref="LogLevel.Trace"/>: the library logs everything except its message sender.</item>
/// <item><see cref="LogLevel.Trace"/> plus <see cref="VncKeystrokesVariable"/>: nothing is filtered.</item>
/// </list>
/// </summary>
internal static class AppLogFilters
{
    /// <summary>The opt-in that lets a Trace log include the VNC library's outgoing messages, keystrokes included.</summary>
    public const string VncKeystrokesVariable = "POLYTERM_LOG_VNC_KEYSTROKES";

    private const string VncLibraryCategory = "MarcusW.VncClient";

    private const string VncMessageSenderCategory =
        "MarcusW.VncClient.Protocol.Implementation.Services.Communication.RfbMessageSender";

    /// <summary>Sets the minimum level and the VNC library filters for it.</summary>
    public static ILoggingBuilder Apply(ILoggingBuilder builder, LogLevel level, bool includeVncKeystrokes)
    {
        builder.SetMinimumLevel(level);

        // A category filter replaces the minimum level rather than adding to it, so at Information
        // and above adding one would make the library louder than the level that was asked for.
        if (level >= LogLevel.Information || IncludesVncKeystrokes(level, includeVncKeystrokes))
            return builder;

        builder.AddFilter(VncMessageSenderCategory, LogLevel.Information);
        if (level == LogLevel.Debug)
            builder.AddFilter(VncLibraryCategory, LogLevel.Information);

        return builder;
    }

    /// <summary>True when keystrokes will reach the log: the opt-in only works at <see cref="LogLevel.Trace"/>.</summary>
    public static bool IncludesVncKeystrokes(LogLevel level, bool optedIn) => optedIn && level == LogLevel.Trace;

    /// <summary>True when the <see cref="VncKeystrokesVariable"/> value is <c>1</c> or <c>true</c>; anything else leaves keystrokes out.</summary>
    public static bool IsOptedIn(string? value) =>
        value?.Trim() is { } trimmed
        && (trimmed == "1" || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase));
}
