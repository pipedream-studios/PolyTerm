using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace PolyTerm.App;

/// <summary>
/// Writes unhandled exceptions to a log file before the process dies. Without this, a crash in an app
/// launched from Finder or the Dock leaves only a macOS .ips report, which can't name .NET frames or show
/// the exception message, and the runtime's own stderr dump goes nowhere when launchd is the parent.
/// <para>
/// Fatal exceptions each get their own <c>crash-*.log</c> (the newest <see cref="MaxCrashLogs"/> are kept);
/// unobserved task exceptions aren't fatal, so they're appended to a single
/// <c>unobserved-task-exceptions.log</c> instead of creating a file each.
/// </para>
/// </summary>
internal static class CrashLog
{
    /// <summary>How many <c>crash-*.log</c> files to keep; older ones are deleted after each new crash.</summary>
    internal const int MaxCrashLogs = 20;

    /// <summary>Hooks <see cref="AppDomain.UnhandledException"/> and <see cref="TaskScheduler.UnobservedTaskException"/>. Call first thing in <c>Main</c>.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var path = WriteCrashReport(e.ExceptionObject as Exception, ResolveLogDirectory(), DateTimeOffset.Now);
            if (path is not null)
                TryWriteStderr($"PolyTerm crashed - details written to {path}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
            AppendUnobserved(e.Exception, ResolveLogDirectory(), DateTimeOffset.Now);
    }

    /// <summary>
    /// Log directory: <c>~/Library/Logs/PolyTerm</c> on macOS (where Console.app looks), otherwise a
    /// <c>logs</c> folder under <see cref="AppPaths.ConfigDirectory"/>. <c>POLYTERM_DATA_DIR</c> wins on
    /// every platform, so test runs never write into the real user's log folder.
    /// </summary>
    internal static string? ResolveLogDirectory()
    {
        try
        {
            if (OperatingSystem.IsMacOS() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POLYTERM_DATA_DIR")))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "PolyTerm");
            return Path.Combine(AppPaths.ConfigDirectory, "logs");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes one crash report and prunes old ones. Returns the file's path, or null if it couldn't be written - never throws.</summary>
    internal static string? WriteCrashReport(Exception? exception, string? directory, DateTimeOffset now)
    {
        if (directory is null) return null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(path, FormatReport("Unhandled exception - PolyTerm terminated", exception, now));
            PruneCrashLogs(directory);
            return path;
        }
        catch
        {
            return null; // a crash handler that throws would only hide the original exception
        }
    }

    /// <summary>Appends a non-fatal unobserved task exception to the shared log. Never throws.</summary>
    internal static void AppendUnobserved(Exception? exception, string? directory, DateTimeOffset now)
    {
        if (directory is null) return;
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "unobserved-task-exceptions.log"),
                FormatReport("Unobserved task exception (not fatal)", exception, now) + Environment.NewLine);
        }
        catch
        {
            // best effort only
        }
    }

    internal static string FormatReport(string heading, Exception? exception, DateTimeOffset now)
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? "unknown";
        var sb = new StringBuilder();
        sb.AppendLine(heading);
        sb.AppendLine(CultureInfo.InvariantCulture, $"Time:     {now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Version:  {version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS:       {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Runtime:  {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Process:  {Environment.ProcessPath} (PID {Environment.ProcessId})");
        sb.AppendLine();
        sb.AppendLine(exception?.ToString() ?? "(no exception object - a non-Exception was thrown)");
        return sb.ToString();
    }

    private static void PruneCrashLogs(string directory)
    {
        // The timestamped names sort chronologically, so ordinal name order is age order.
        var stale = Directory.GetFiles(directory, "crash-*.log")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Skip(MaxCrashLogs);
        foreach (var file in stale)
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    private static void TryWriteStderr(string message)
    {
        try { Console.Error.WriteLine(message); } catch { /* best effort */ }
    }
}
