using System.Runtime.CompilerServices;
using System.Threading;

namespace PolyTerm.App.Tests;

/// <summary>
/// Issue #221: App.Tests intermittently reports an xUnit "catastrophic failure" (NRE on Windows,
/// ObjectDisposedException on a ReaderWriterLockSlim elsewhere) with no stack trace, while every test
/// passes. This writes the full exception, stack included, to stderr and to
/// <c>TestResults/app-tests-unhandled-*.log</c> (uploaded by CI) whenever one escapes on a background
/// thread or as an unobserved task.
/// </summary>
internal static class UnhandledExceptionDiagnostics
{
    private static int s_count;

    [ModuleInitializer]
    internal static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("AppDomain.UnhandledException", e.ExceptionObject as Exception, $"IsTerminating={e.IsTerminating}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Report("TaskScheduler.UnobservedTaskException", e.Exception, null);

        // The catastrophic failure itself is caught inside xUnit (MessageBus.DispatchMessages or a
        // runner's OnError) and reported as an ErrorMessage, so neither hook above sees it, and the
        // VSTest adapter prints that message without its stack. Catch the two observed signatures at
        // the throw point instead; at first chance the exception's own StackTrace is only one frame,
        // so record the full current stack alongside it.
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (t_inFirstChance) return;
            var ex = e.Exception;
            var interesting =
                (ex is ObjectDisposedException ode && ode.ObjectName == typeof(ReaderWriterLockSlim).FullName) ||
                ex is NullReferenceException;
            if (!interesting) return;
            t_inFirstChance = true;
            try { Report("FirstChanceException", ex, $"{Environment.NewLine}Full stack at throw:{Environment.NewLine}{new System.Diagnostics.StackTrace(1, true)}"); }
            finally { t_inFirstChance = false; }
        };
    }

    [ThreadStatic] private static bool t_inFirstChance;

    internal static void Report(string source, Exception? ex, string? extra)
    {
        var n = Interlocked.Increment(ref s_count);
        var text =
            $"[#221 diagnostics] {source} #{n} at {DateTime.UtcNow:O} on thread {Environment.CurrentManagedThreadId} " +
            $"('{Thread.CurrentThread.Name}') {extra}{Environment.NewLine}{ex}{Environment.NewLine}";
        try { Console.Error.WriteLine(text); } catch { /* best effort */ }
        try
        {
            var dir = Environment.GetEnvironmentVariable("POLYTERM_APP_TESTS_DIAG_DIR")
                      ?? Path.Combine(AppContext.BaseDirectory, "TestResults");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"app-tests-unhandled-{Environment.ProcessId}.log"), text);
        }
        catch { /* best effort */ }
    }
}
