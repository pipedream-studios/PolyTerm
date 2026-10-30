using System.Text.RegularExpressions;

namespace PolyTerm.App.McpBridge;

/// <summary>The shell family behind a terminal session, as far as <c>terminal_execute</c> needs to know it.</summary>
public enum ShellKind
{
    /// <summary>Not a recognised shell (a router CLI, a REPL, csh...) - fall back to waiting for output to settle.</summary>
    Unknown,
    /// <summary>bash, zsh, sh/dash, ksh.</summary>
    Posix,
    Fish,
    PowerShell,
    Cmd,
}

/// <summary>A command with its completion marker appended, plus the appended part on its own so it can be
/// removed from the echoed command line before output is returned.</summary>
internal sealed record WrappedCommand(string Text, string Suffix);

/// <summary>
/// How <c>terminal_execute</c> knows a command has finished, and with what exit code. Waiting for output to
/// go quiet returns early from anything that pauses (<c>sleep 2</c>, a slow build step) and can't report an
/// exit code, so the command is sent with a marker appended - <c>; echo "__PT_&lt;token&gt;_$?__"</c> for a
/// POSIX shell - and the call completes when that marker's output line arrives. The marker's syntax depends
/// on the shell (fish has no <c>$?</c> and rejects the whole line if it sees one), so each session's shell is
/// identified first with <see cref="ProbeCommand"/>.
/// <para>
/// The echoed command line contains the marker's <em>source</em> (<c>$?</c>, <c>%^ERRORLEVEL%</c>...) rather
/// than digits, so it never matches <see cref="TryFindMarker"/>.
/// </para>
/// </summary>
internal static class ShellSentinel
{
    public static string NewToken() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// One line that's valid and harmless in every supported shell, printing something different in each:
    /// bash/zsh <c>__PT_SH_tok_/</c>, fish <c>__PT_SH_tok_3.7.1/</c> (<c>$FISH_VERSION</c> is set), PowerShell
    /// <c>__PT_SH_tok_/System.Management...</c> (<c>$PSVersionTable</c>), cmd the quoted text verbatim.
    /// </summary>
    public static string ProbeCommand(string token) => $"echo \"__PT_SH_{token}_$FISH_VERSION/$PSVersionTable\"";

    /// <summary>Finds <see cref="ProbeCommand"/>'s output in <paramref name="lines"/>; null until it has arrived.</summary>
    public static ShellKind? ParseProbe(IEnumerable<string> lines, string token)
    {
        var prefix = $"__PT_SH_{token}_";
        foreach (var raw in lines)
        {
            // The output line starts with the marker; the echoed command line has a prompt and "echo " first.
            var line = raw.Trim();
            if (line.StartsWith('"' + prefix, StringComparison.Ordinal))
                return ShellKind.Cmd; // cmd's echo prints the quotes and leaves the variables unexpanded
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var rest  = line[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0) return ShellKind.Unknown;
            if (rest[(slash + 1)..].Length > 0) return ShellKind.PowerShell;
            return slash > 0 ? ShellKind.Fish : ShellKind.Posix;
        }
        return null;
    }

    /// <summary>
    /// <paramref name="command"/> with a marker that prints <c>__PT_&lt;token&gt;_&lt;exit code&gt;__</c> once it
    /// finishes. Appended on the same line, so a command that reads stdin doesn't get the marker as input.
    /// PowerShell reports 0 if <c>$?</c> is true, else <c>$LASTEXITCODE</c> (a native command's code), else 1.
    /// </summary>
    public static WrappedCommand Wrap(string command, ShellKind kind, string token)
    {
        var cmd = command.TrimEnd();
        string suffix;
        switch (kind)
        {
            case ShellKind.Posix:
                cmd    = TrimTrailingSemicolons(cmd);
                suffix = $"{Separator(cmd)}echo \"__PT_{token}_$?__\"";
                break;
            case ShellKind.Fish:
                cmd    = TrimTrailingSemicolons(cmd);
                suffix = $"{Separator(cmd)}echo \"__PT_{token}_\"$status\"__\"";
                break;
            case ShellKind.PowerShell:
                cmd    = TrimTrailingSemicolons(cmd);
                suffix = $"; \"__PT_{token}_$(if ($?) {{ 0 }} elseif ($LASTEXITCODE) {{ $LASTEXITCODE }} else {{ 1 }})__\"";
                break;
            case ShellKind.Cmd:
                // %ERRORLEVEL% would expand when the line is parsed, before the command runs; "call" plus the
                // caret defers it to when the echo itself runs.
                suffix = $" & call echo __PT_{token}_%^ERRORLEVEL%__";
                break;
            default:
                return new WrappedCommand(command, "");
        }
        return new WrappedCommand(cmd + suffix, suffix);
    }

    /// <summary>
    /// Looks for the marker in <paramref name="lines"/>. <paramref name="markerLine"/> is its line's index and
    /// <paramref name="before"/> any text on that line ahead of it (output that didn't end in a newline).
    /// </summary>
    public static bool TryFindMarker(IReadOnlyList<string> lines, string token, out int exitCode, out int markerLine, out string before)
    {
        var pattern = MarkerPattern(token);
        for (var i = 0; i < lines.Count; i++)
        {
            var m = pattern.Match(lines[i]);
            if (!m.Success) continue;
            exitCode   = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            markerLine = i;
            before     = lines[i][..m.Index];
            return true;
        }
        exitCode = 0;
        markerLine = -1;
        before = "";
        return false;
    }

    /// <summary>
    /// The command's output as the caller should see it: lines up to the marker, the marker removed, and
    /// <paramref name="suffix"/> removed from the echoed command line so it reads as the command that was asked for.
    /// </summary>
    public static IEnumerable<string> CleanOutput(IReadOnlyList<string> lines, int markerLine, string before, string suffix)
    {
        for (var i = 0; i < markerLine; i++)
            yield return suffix.Length > 0 ? lines[i].Replace(suffix, "", StringComparison.Ordinal) : lines[i];
        if (before.Trim().Length > 0)
            yield return before;
    }

    /// <summary>
    /// Whether <paramref name="command"/> may leave the session in a different shell (or none) - starting one,
    /// <c>ssh</c>/<c>su</c>/<c>sudo -i</c> to another, or <c>exit</c> back out - so the cached shell kind is stale.
    /// </summary>
    public static bool MayChangeShell(string command)
    {
        var first = command.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        first = Path.GetFileNameWithoutExtension(first.Replace('\\', '/')).ToLowerInvariant();
        return first is "bash" or "sh" or "zsh" or "fish" or "dash" or "ksh" or "nu" or "pwsh" or "powershell" or "cmd"
            or "ssh" or "mosh" or "telnet" or "su" or "sudo" or "doas" or "exit" or "logout" or "wsl"
            or "docker" or "podman" or "kubectl" or "tmux" or "screen";
    }

    // "cmd &; echo" is a syntax error in bash, so a backgrounded command is followed with a space instead.
    private static string Separator(string cmd) =>
        cmd.EndsWith('&') && !cmd.EndsWith("&&", StringComparison.Ordinal) ? " " : "; ";

    // "ls;; echo" is a syntax error too.
    private static string TrimTrailingSemicolons(string cmd) => cmd.TrimEnd(';').TrimEnd();

    private static Regex MarkerPattern(string token) => new($"__PT_{Regex.Escape(token)}_(-?\\d+)__", RegexOptions.CultureInvariant);
}
