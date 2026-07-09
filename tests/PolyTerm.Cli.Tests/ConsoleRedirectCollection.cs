using Xunit;

namespace PolyTerm.Cli.Tests;

/// <summary>
/// Every test class in this project redirects <see cref="Console.Out"/>/<see cref="Console.Error"/>
/// (see <see cref="ConsoleCapture"/>) — process-wide static state, so all of them need to run
/// sequentially relative to each other, not just within a single class (xUnit's default). This
/// collection (applied to every class here) disables cross-class parallelism for exactly that reason.
/// </summary>
[CollectionDefinition("ConsoleRedirect", DisableParallelization = true)]
public sealed class ConsoleRedirectCollection;
