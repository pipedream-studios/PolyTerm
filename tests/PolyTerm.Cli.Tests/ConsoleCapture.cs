using System.CommandLine;

namespace PolyTerm.Cli.Tests;

/// <summary>
/// Wraps a single built <see cref="Command"/> in a throwaway <see cref="RootCommand"/>, invokes it
/// with the given arguments, and captures whatever it wrote to <see cref="Console.Out"/>/
/// <see cref="Console.Error"/> — every CLI command in this repo writes directly to those static
/// properties rather than through System.CommandLine's own <c>IConsole</c> abstraction, so redirecting
/// them for the duration of the call is the only way to capture output in-process.
/// </summary>
internal static class ConsoleCapture
{
    public static Task<(int ExitCode, string Out, string Error)> RunAsync(Command command, params string[] args) =>
        RunAsync(command, CancellationToken.None, args);

    /// <summary>
    /// Same as the params-array overload, but takes an explicit <see cref="CancellationToken"/> — needed
    /// to simulate Ctrl+C against a command whose action loops until cancelled (e.g. <c>attach</c>'s
    /// terminal/framebuffer poll loops), since there's no real console to send a physical Ctrl+C to in
    /// a test process. <c>ParseResult.InvokeAsync</c> forwards this token as the exact same
    /// <see cref="CancellationToken"/> the command's own action receives as its second parameter.
    /// </summary>
    public static async Task<(int ExitCode, string Out, string Error)> RunAsync(Command command, CancellationToken cancellationToken, params string[] args)
    {
        var root = new RootCommand { command };
        var fullArgs = new[] { command.Name }.Concat(args).ToArray();

        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errWriter);
        try
        {
            var exitCode = await root.Parse(fullArgs).InvokeAsync(cancellationToken: cancellationToken);
            return (exitCode, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
