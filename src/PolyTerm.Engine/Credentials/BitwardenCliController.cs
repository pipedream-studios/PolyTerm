using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.Credentials;

/// <summary>Result of parsing <c>bw status</c>'s JSON output.</summary>
public enum BitwardenAuthStatus
{
    /// <summary><c>bw status</c> failed, returned no usable output, or the CLI isn't installed/reachable.</summary>
    Unknown,
    Unauthenticated,
    Locked,
    Unlocked,
}

/// <summary>Outcome of a login or unlock attempt.</summary>
public sealed record BitwardenCliResult(bool Success, string? ErrorMessage, bool TwoFactorRequired = false);

/// <summary>
/// Drives the Bitwarden CLI (<c>bw</c>) directly so PolyTerm can get its own vault into a usable state
/// instead of requiring the user to run <c>bw login</c>/<c>bw unlock</c>/<c>bw serve</c> in an external
/// terminal and keep that process alive by hand (issue #78). <see cref="BitwardenProvider"/> itself is
/// untouched — it still talks to <c>bw serve</c> over HTTP exactly as before; this class is only
/// responsible for getting a session key and keeping a <c>bw serve</c> child process alive with it.
/// </summary>
public sealed class BitwardenCliController : IDisposable
{
    private readonly BitwardenProviderOptions _options;
    private readonly ILogger<BitwardenCliController> _logger;
    private readonly object _serveLock = new();
    private Process? _serveProcess;

    public BitwardenCliController(BitwardenProviderOptions options, ILogger<BitwardenCliController> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Runs <c>bw status</c> and parses its JSON output. Returns <see cref="BitwardenAuthStatus.Unknown"/> if the CLI can't be run or its output can't be parsed.</summary>
    public async Task<BitwardenAuthStatus> CheckStatusAsync(CancellationToken ct = default)
    {
        var (exitCode, stdout, _) = await RunCliAsync(["status"], env: null, ct).ConfigureAwait(false);
        return exitCode == 0 ? ParseStatus(stdout) : BitwardenAuthStatus.Unknown;
    }

    /// <summary>Parses <c>bw status</c>'s <c>{"status": "..."}</c> JSON into a <see cref="BitwardenAuthStatus"/>. Exposed for testing.</summary>
    internal static BitwardenAuthStatus ParseStatus(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return BitwardenAuthStatus.Unknown;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            return status switch
            {
                "unlocked" => BitwardenAuthStatus.Unlocked,
                "locked" => BitwardenAuthStatus.Locked,
                "unauthenticated" => BitwardenAuthStatus.Unauthenticated,
                _ => BitwardenAuthStatus.Unknown,
            };
        }
        catch (JsonException)
        {
            return BitwardenAuthStatus.Unknown;
        }
    }

    /// <summary>
    /// Logs in with an email + master password (<c>bw login &lt;email&gt; --passwordenv BW_PASSWORD --raw</c>,
    /// optionally with <c>--code</c> for a 2FA-required account) and, on success, starts the managed
    /// <c>bw serve</c> process with the resulting session key. The password is passed via a child-process
    /// environment variable, never as a command-line argument, so it never appears in the process list.
    /// </summary>
    public async Task<BitwardenCliResult> LoginAsync(string email, string masterPassword, string? twoFactorCode, CancellationToken ct = default)
    {
        List<string> args = ["login", email, "--passwordenv", "BW_PASSWORD", "--raw"];
        if (!string.IsNullOrEmpty(twoFactorCode))
            args.AddRange(["--code", twoFactorCode]);

        var (exitCode, stdout, stderr) = await RunCliAsync(
            args, new Dictionary<string, string> { ["BW_PASSWORD"] = masterPassword }, ct).ConfigureAwait(false);
        return await FinishAuthAsync(exitCode, stdout, stderr, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unlocks an already-logged-in vault (<c>bw unlock --passwordenv BW_PASSWORD --raw</c>) and, on
    /// success, starts the managed <c>bw serve</c> process with the resulting session key.
    /// </summary>
    public async Task<BitwardenCliResult> UnlockAsync(string masterPassword, CancellationToken ct = default)
    {
        var (exitCode, stdout, stderr) = await RunCliAsync(
            ["unlock", "--passwordenv", "BW_PASSWORD", "--raw"],
            new Dictionary<string, string> { ["BW_PASSWORD"] = masterPassword }, ct).ConfigureAwait(false);
        return await FinishAuthAsync(exitCode, stdout, stderr, ct).ConfigureAwait(false);
    }

    private async Task<BitwardenCliResult> FinishAuthAsync(int exitCode, string stdout, string stderr, CancellationToken ct)
    {
        if (exitCode != 0)
        {
            var twoFactorRequired = stderr.Contains("Two-step login", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Two-factor", StringComparison.OrdinalIgnoreCase);
            return new BitwardenCliResult(
                false,
                twoFactorRequired ? "Two-factor authentication code required." : FirstNonEmptyLine(stderr, "bw command failed."),
                twoFactorRequired);
        }

        var sessionKey = stdout.Trim();
        if (string.IsNullOrEmpty(sessionKey))
            return new BitwardenCliResult(false, "bw did not return a session key.");

        var started = await EnsureServeRunningAsync(sessionKey, ct).ConfigureAwait(false);
        return started
            ? new BitwardenCliResult(true, null)
            : new BitwardenCliResult(false, "Logged in, but failed to start 'bw serve' with the new session.");
    }

    private static string FirstNonEmptyLine(string text, string fallback) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fallback;

    /// <summary>
    /// Kills any previously-managed <c>bw serve</c> process and starts a new one bound to the configured
    /// <see cref="BitwardenProviderOptions.BaseUrl"/> host/port with <paramref name="sessionKey"/>, then
    /// polls its <c>/status</c> endpoint until it responds (or times out after 5s).
    /// </summary>
    private async Task<bool> EnsureServeRunningAsync(string sessionKey, CancellationToken ct)
    {
        var baseUri = new Uri(_options.BaseUrl);

        lock (_serveLock)
        {
            KillServeProcess();

            var psi = new ProcessStartInfo(_options.CliPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("serve");
            psi.ArgumentList.Add("--hostname");
            psi.ArgumentList.Add(baseUri.Host);
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(baseUri.Port.ToString(CultureInfo.InvariantCulture));
            psi.Environment["BW_SESSION"] = sessionKey;

            try
            {
                _serveProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start 'bw serve' at '{Path}'.", _options.CliPath);
                return false;
            }
        }

        for (var i = 0; i < 20; i++)
        {
            if (_serveProcess is { HasExited: true }) return false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var resp = await http.GetAsync(new Uri(baseUri, "status"), ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch
            {
                // Not up yet — keep polling until the timeout below.
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Stops the managed <c>bw serve</c> process, if one is running. Safe to call repeatedly.</summary>
    public void StopServe()
    {
        lock (_serveLock)
        {
            KillServeProcess();
        }
    }

    private void KillServeProcess()
    {
        if (_serveProcess is { HasExited: false })
        {
            try { _serveProcess.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to kill previous 'bw serve' process."); }
        }
        _serveProcess?.Dispose();
        _serveProcess = null;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_options.CliPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (env is not null)
            foreach (var (key, value) in env) psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            _logger.LogWarning(ex, "Failed to launch bw CLI at '{Path}'.", _options.CliPath);
            return (-1, "", $"Could not launch '{_options.CliPath}' — is the Bitwarden CLI installed and on PATH?");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    /// <summary>Stops the managed <c>bw serve</c> process. Call on app shutdown to avoid an orphaned child process.</summary>
    public void Dispose() => StopServe();
}
