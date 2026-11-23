using Microsoft.Extensions.Logging;

namespace PolyTerm.App;

/// <summary>
/// Resolves the app's minimum log level (issue #281). Release builds default to
/// <see cref="LogLevel.Information"/> and Debug builds to <see cref="LogLevel.Debug"/>;
/// <c>POLYTERM_LOG_LEVEL</c> overrides either with any <see cref="LogLevel"/> name
/// (case-insensitive, e.g. <c>Trace</c>, <c>debug</c>, <c>Warning</c>, <c>None</c>), so a shipped
/// build can be troubleshot from a terminal without rebuilding.
/// </summary>
internal static class AppLogLevel
{
    /// <summary>The environment variable that overrides the build's default level.</summary>
    public const string EnvironmentVariable = "POLYTERM_LOG_LEVEL";

    /// <summary>The level used when <see cref="EnvironmentVariable"/> is unset or invalid.</summary>
    public const LogLevel BuildDefault =
#if DEBUG
        LogLevel.Debug;
#else
        LogLevel.Information;
#endif

    /// <summary>
    /// Resolves the minimum level from an override value (normally the <see cref="EnvironmentVariable"/>
    /// value). Returns <paramref name="defaultLevel"/> when the value is null, blank, or not a
    /// <see cref="LogLevel"/> name; <paramref name="invalid"/> is true only in that last case, so the
    /// caller can warn about a typo instead of silently ignoring it.
    /// </summary>
    public static LogLevel Resolve(string? overrideValue, LogLevel defaultLevel, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrWhiteSpace(overrideValue))
            return defaultLevel;

        // Match names only: Enum.TryParse would also accept numbers such as "3" or "42", and an
        // out-of-range number would produce an undefined LogLevel.
        var trimmed = overrideValue.Trim();
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            if (string.Equals(level.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                return level;
        }

        invalid = true;
        return defaultLevel;
    }
}
