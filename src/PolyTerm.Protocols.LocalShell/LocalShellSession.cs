using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;
using PolyTerm.Protocols.LocalShell.Platform;

namespace PolyTerm.Protocols.LocalShell;

/// <summary>
/// A session running a local shell process through a platform pseudo-terminal (ConPTY on Windows,
/// <c>script</c>-based PTY on Unix). Has no remote endpoint, so file transfer, forwarding, and reconnect
/// are all unsupported.
/// </summary>
public sealed class LocalShellSession : IProtocolSession
{
    private readonly SessionContext _ctx;
    private readonly ILogger<LocalShellSession> _logger;

    private LocalShellTerminalChannel? _terminal;
    private SessionState _state = SessionState.Created;

    public Guid SessionId => _ctx.SessionId;
    public ConnectionProfile Profile => _ctx.Profile;
    public SessionState State => _state;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;

    /// <summary>The shell terminal channel, available once connected.</summary>
    public ITerminalChannel? Terminal => _terminal;
    public IFileTransferChannel? FileTransfer => null;
    public IFramebufferChannel? Framebuffer => null;
    public ITunnelManager? Tunnels => null;
    public INativeHostChannel? NativeHost => null;

    public LocalShellSession(SessionContext ctx, ILogger<LocalShellSession> logger)
    {
        _ctx    = ctx;
        _logger = logger;
    }

    /// <summary>
    /// Starts the configured shell (or platform default) in a new pseudo-terminal. Synchronous under the hood;
    /// returns a completed task. On failure, transitions to <see cref="SessionState.Faulted"/> and raises
    /// <see cref="ErrorOccurred"/> before rethrowing.
    /// </summary>
    public Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Connecting);
        try
        {
            var settings   = _ctx.Profile.Settings;
            var shell      = GetSetting(settings, "shell") ?? DefaultShell();
            var workingDir = GetSetting(settings, "workingDir");

            IPseudoTerminal pty = CreatePty(shell, workingDir);

            _terminal = new LocalShellTerminalChannel(pty);
            SetState(SessionState.Connected);
            _logger.LogInformation("Local shell started: {Shell}", shell);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local shell failed to start.");
            SetState(SessionState.Faulted);
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs { Exception = ex, IsFatal = true });
            throw;
        }
        return Task.CompletedTask;
    }

    /// <summary>Disposes the terminal channel, which kills the shell process.</summary>
    public Task DisconnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Closing);
        _terminal?.Dispose();
        SetState(SessionState.Closed);
        return Task.CompletedTask;
    }

    /// <summary>Disconnects if not already closed/faulted, then disposes the terminal channel.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_state is not (SessionState.Closed or SessionState.Faulted))
            await DisconnectAsync().ConfigureAwait(false);
        _terminal?.Dispose();
    }

    private void SetState(SessionState next)
    {
        var prev = _state;
        _state = next;
        if (prev != next)
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs { Previous = prev, Current = next });
    }

    private static IPseudoTerminal CreatePty(string shell, string? workingDir)
    {
        if (OperatingSystem.IsWindows())
            return CreateWindowsPtyGuarded(shell, workingDir);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return CreateUnixPtyGuarded(shell, workingDir);
        throw new PlatformNotSupportedException("Local shell not supported on this OS.");
    }

    [SupportedOSPlatform("windows")]
    private static IPseudoTerminal CreateWindowsPtyGuarded(string shell, string? workingDir) =>
        new ConPtyTerminal(shell, workingDir, 80, 24);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static IPseudoTerminal CreateUnixPtyGuarded(string shell, string? workingDir) =>
        new UnixPtyTerminal(shell, workingDir, 80, 24);

    private static string DefaultShell() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "powershell.exe"
            : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";

    private static string? GetSetting(Dictionary<string, JsonElement> settings, string key) =>
        settings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
