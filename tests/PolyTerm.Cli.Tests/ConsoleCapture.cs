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
    public static async Task<(int ExitCode, string Out, string Error)> RunAsync(Command command, params string[] args)
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
            var exitCode = await root.InvokeAsync(fullArgs);
            return (exitCode, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
