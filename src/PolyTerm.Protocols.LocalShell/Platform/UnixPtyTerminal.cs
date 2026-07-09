using System.Diagnostics;
using System.Runtime.Versioning;

namespace PolyTerm.Protocols.LocalShell.Platform;

/// <summary>
/// Unix PTY implementation using the system shell with /dev/tty.
/// Uses script(1) or a thin native helper (openpty) for a proper PTY.
/// For M0 this is a simplified process-based approach; a full openpty binding comes later.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class UnixPtyTerminal : IPseudoTerminal
{
    private readonly Process _process;
    private bool _disposed;

    /// <inheritdoc/>
    public Stream Input => _process.StandardInput.BaseStream;

    /// <inheritdoc/>
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <summary>Starts <paramref name="shell"/> under <c>script</c> to allocate a PTY.</summary>
    public UnixPtyTerminal(string shell, string? workingDir, int cols, int rows)
    {
        // Use 'script' with a PTY to get proper terminal behaviour.
        // 'script -q -c <shell> /dev/null' allocates a PTY and runs the shell.
        var psi = new ProcessStartInfo("script", $"-q -c \"{shell}\" /dev/null")
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = false,
            UseShellExecute        = false,
            WorkingDirectory       = workingDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        // Set terminal size via environment
        psi.Environment["COLUMNS"] = cols.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["LINES"]   = rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["TERM"]    = "xterm-256color";

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Start();
    }

    /// <summary>
    /// Not yet implemented: intended to notify the shell of a window size change via SIGWINCH/ioctl(TIOCSWINSZ),
    /// but currently a no-op. See the M0 note on <see cref="UnixPtyTerminal"/> — this is a placeholder pending
    /// a native openpty binding.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        // Send SIGWINCH to the process (requires P/Invoke on Linux/macOS; placeholder)
        // Full implementation: ioctl(TIOCSWINSZ)
    }

    /// <inheritdoc/>
    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    /// <summary>Kills the shell process if still running, then disposes it. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_process.HasExited) _process.Kill(); } catch { }
        _process.Dispose();
    }
}
