using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using PolyTerm.Engine;
using PolyTerm.Protocols.Ssh.X11;

namespace PolyTerm.Protocols.Ssh;

/// <summary>
/// An SSH connection, including terminal shell, best-effort SFTP, port forwarding, and optional
/// jump-host chaining. Handles auth method selection, host-key verification, and automatic
/// reconnection with exponential backoff if the connection drops unexpectedly.
/// </summary>
public sealed class SshSession : IProtocolSession
{
    private readonly SessionContext _ctx;
    private readonly ILogger<SshSession> _logger;

    private SshClient? _client;
    private SftpClient? _sftp;
    private ShellStream? _shell;
    private ITerminalChannel? _terminal;
    private X11Forwarder? _x11Forwarder;
    private SshFileTransferChannel? _transfer;
    private SshTunnelManager? _tunnelManager;
    private readonly List<SshClient> _jumpClients = [];
    private readonly List<ForwardedPortLocal> _jumpForwards = [];
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;

    private SessionState _state = SessionState.Created;

    public Guid SessionId => _ctx.SessionId;
    public ConnectionProfile Profile => _ctx.Profile;
    public SessionState State => _state;

    /// <summary>Raised whenever <see cref="State"/> changes, including transitions into/out of <see cref="SessionState.Reconnecting"/>.</summary>
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>Raised on fatal connect failures. Reconnect attempt failures are logged, not raised here.</summary>
    public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;

    /// <summary>The shell terminal channel, available once connected. Null before connect and while reconnecting.</summary>
    public ITerminalChannel? Terminal => _terminal;

    /// <summary>The SFTP file-transfer channel. Null if SFTP failed to connect (best-effort — does not abort the session).</summary>
    public IFileTransferChannel? FileTransfer => _transfer;
    public IFramebufferChannel? Framebuffer => null;

    /// <summary>Manages local/remote/dynamic port forwards over this connection.</summary>
    public ITunnelManager? Tunnels => _tunnelManager;
    public INativeHostChannel? NativeHost => null;

    public SshSession(SessionContext ctx, ILogger<SshSession> logger)
    {
        _ctx    = ctx;
        _logger = logger;
    }

    /// <summary>
    /// Connects using the credential and jump chain supplied in the <see cref="SessionContext"/>.
    /// On failure, transitions to <see cref="SessionState.Faulted"/> and raises <see cref="ErrorOccurred"/> before rethrowing.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Connecting);
        try
        {
            await ConnectCoreAsync(_ctx.Credential, _ctx.JumpChain, ct).ConfigureAwait(false);
            SetState(SessionState.Connected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH connect failed.");
            SetState(SessionState.Faulted);
            var reported = DescribeConnectFailure(ex);
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs { Exception = reported, IsFatal = true });
            throw reported;
        }
    }

    /// <summary>
    /// Translates common low-level connect failures (socket errors, SSH protocol/auth errors) into a
    /// short, user-facing message, wrapping the original as <see cref="Exception.InnerException"/> for
    /// diagnostics — the original is already logged in full by the caller. Anything not recognized is
    /// passed through unchanged so its message isn't silently lost.
    /// </summary>
    private static Exception DescribeConnectFailure(Exception ex)
    {
        var message = ex switch
        {
            SocketException { SocketErrorCode: SocketError.TimedOut } =>
                "Connection timed out — the host did not respond.",
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
                "Connection refused — is SSH running on that host and port?",
            SocketException { SocketErrorCode: SocketError.HostNotFound } =>
                "Host not found — check the hostname.",
            SocketException =>
                $"Network error: {ex.Message}",
            HostKeyRejectedException =>
                "Host key was rejected — the server's identity could not be verified.",
            SshAuthenticationException =>
                "Authentication failed — check the username, password, or key.",
            ProxyException =>
                $"Jump host connection failed: {ex.Message}",
            SshConnectionException =>
                $"SSH connection failed: {ex.Message}",
            _ => null,
        };

        return message is null ? ex : new SshConnectException(message, ex);
    }

    /// <summary>
    /// Establishes the jump-host chain (if any), then the primary connection, shell, best-effort SFTP,
    /// and auto-start tunnels. Used both by the initial <see cref="ConnectAsync"/> and by <see cref="ReconnectLoopAsync"/>,
    /// which is why credential/jump-chain material is passed in rather than read from <see cref="_ctx"/> directly —
    /// a reconnect attempt supplies freshly resolved material instead of the single-use initial values.
    /// </summary>
    private async Task ConnectCoreAsync(
        CredentialMaterial? credential, IReadOnlyList<JumpHopContext> jumpChain, CancellationToken ct)
    {
        var profile  = _ctx.Profile;
        var host     = profile.Host ?? throw new InvalidOperationException("No host configured.");
        var port     = profile.Port ?? 22;
        var username = GetSetting(profile.Settings, "username") ?? Environment.UserName;
        var termType = GetSetting(profile.Settings, "terminalType") ?? "xterm-256color";
        var keepAlive = GetIntSetting(profile.Settings, "keepAliveSeconds") ?? 30;

        var authMethods = BuildAuthMethods(username, credential, profile.Settings).ToList();
        _logger.LogDebug("SSH auth methods: {Methods}",
            string.Join(", ", authMethods.Select(m => m.GetType().Name)));

        // Jump-host chain: connect each hop in turn, forwarding a loopback port to the
        // next hop's (or, for the last hop, the real target's) host:port. SSH.NET has no
        // native ProxyJump support, so each leg is a real SshClient connected through the
        // previous leg's ForwardedPortLocal.
        (string Host, int Port)? tunnelEndpoint = null;
        for (var i = 0; i < jumpChain.Count; i++)
        {
            var hop = jumpChain[i];
            var hopHost = hop.Profile.Host ?? throw new InvalidOperationException("Jump host has no host configured.");
            var hopPort = hop.Profile.Port ?? 22;
            var hopUsername = GetSetting(hop.Profile.Settings, "username") ?? Environment.UserName;
            var hopKeepAlive = GetIntSetting(hop.Profile.Settings, "keepAliveSeconds") ?? 30;

            var (connectHost, connectPort) = tunnelEndpoint ?? (hopHost, hopPort);
            var hopAuth = BuildAuthMethods(hopUsername, hop.Credential, hop.Profile.Settings).ToList();
            var hopConnInfo = new ConnectionInfo(connectHost, connectPort, hopUsername, [.. hopAuth]);

            var hopClient = new SshClient(hopConnInfo);
            hopClient.KeepAliveInterval = TimeSpan.FromSeconds(hopKeepAlive);
            WireHostKeyVerification(hopClient, hopHost, hopPort, ct);
            await Task.Run(() => hopClient.Connect(), ct).ConfigureAwait(false);
            _jumpClients.Add(hopClient);

            var (nextRealHost, nextRealPort) = i + 1 < jumpChain.Count
                ? (jumpChain[i + 1].Profile.Host!, jumpChain[i + 1].Profile.Port ?? 22)
                : (host, port);

            var forward = new ForwardedPortLocal("127.0.0.1", 0, nextRealHost, (uint)nextRealPort);
            hopClient.AddForwardedPort(forward);
            forward.Start();
            _jumpForwards.Add(forward);
            tunnelEndpoint = ("127.0.0.1", (int)forward.BoundPort);

            _logger.LogInformation("Connected jump hop {Host}:{Port}, forwarding to {NextHost}:{NextPort}",
                hopHost, hopPort, nextRealHost, nextRealPort);
        }

        var (finalHost, finalPort) = tunnelEndpoint ?? (host, port);
        var connInfo = new ConnectionInfo(finalHost, finalPort, username, [.. authMethods]);

        // Capture the SSH login banner (RFC 4252 §5.4) shown before authentication completes.
        string? loginBanner = null;
        connInfo.AuthenticationBanner += (_, e) => loginBanner = e.BannerMessage;

        _client = new SshClient(connInfo);
        _client.KeepAliveInterval = TimeSpan.FromSeconds(keepAlive);
        _client.ErrorOccurred += OnClientError;

        // Host key verification is always checked against the real target host:port,
        // even when the actual TCP connection goes to a loopback jump-chain endpoint.
        WireHostKeyVerification(_client, host, port, ct);

        await Task.Run(() => _client.Connect(), ct).ConfigureAwait(false);

        var termCols = 80u; var termRows = 24u;
        if (GetBoolSetting(profile.Settings, "x11Forwarding"))
        {
            // X11 forwarding needs "x11-req" sent before "shell" so the remote shell inherits
            // DISPLAY, which ShellStream's constructor has no hook for — see SshX11ShellChannel's
            // doc comment and docs/todos.md's SSH X11 section for the full story (confirmed live
            // against a real sshd; not confirmed against a real X11 app — no Linux/macOS sshd or X
            // server available in this environment).
            var session = SshNetInternals.GetSession(_client)
                ?? throw new InvalidOperationException("SSH session unavailable after connect.");
            var cookie = new byte[16];
            Random.Shared.NextBytes(cookie);
            var x11Params = new X11ForwardingParameters(
                SingleConnection: false,
                AuthenticationProtocol: "MIT-MAGIC-COOKIE-1",
                AuthenticationCookieHex: Encoding.ASCII.GetBytes(Convert.ToHexString(cookie).ToLowerInvariant()),
                ScreenNumber: 0u);
            _terminal = new SshX11ShellChannel(session, termType, termCols, termRows, x11Params, loginBanner);

            _x11Forwarder = new X11Forwarder(session, _logger, X11DisplayEndpoint.ResolveFromEnvironment(), cookie);
            _x11Forwarder.Start();
        }
        else
        {
            _shell    = _client.CreateShellStream(termType, termCols, termRows, 0, 0, 4096);
            _terminal = new SshTerminalChannel(_shell, termType, loginBanner);
        }

        // SFTP is best-effort for M0 — failure does not abort the session
        try
        {
            _sftp = new SftpClient(connInfo);
            await Task.Run(() => _sftp.Connect(), ct).ConfigureAwait(false);
            _transfer = new SshFileTransferChannel(_sftp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SFTP connection failed; file transfer unavailable for this session.");
            _sftp?.Dispose();
            _sftp = null;
        }

        _tunnelManager = new SshTunnelManager(_client, _logger);
        foreach (var def in profile.Settings.GetTunnels().Where(d => d.AutoStart))
        {
            try
            {
                await _tunnelManager.OpenAsync(def, ct).ConfigureAwait(false);
                _logger.LogInformation("Auto-started {Kind} tunnel {Name} on {Bind}:{Port}",
                    def.Kind, def.Name ?? "(unnamed)", def.BindAddress, def.BindPort);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-start tunnel {Name} ({Kind}); continuing without it.",
                    def.Name ?? "(unnamed)", def.Kind);
            }
        }

        _logger.LogInformation("SSH connected to {Host}:{Port}", host, port);
    }

    /// <summary>
    /// Runs while <see cref="State"/> is <see cref="SessionState.Reconnecting"/>: tears down the stale connection,
    /// then retries <see cref="ConnectCoreAsync"/> with exponential backoff (2s, 4s, 8s, ... capped at 30s) until
    /// it succeeds or <paramref name="ct"/> is cancelled (via <see cref="DisconnectAsync"/>). Each attempt calls
    /// <see cref="SessionContext.RefreshCredentialsAsync"/> for fresh, single-use credential material, since the
    /// original credential/jump-chain material was already consumed and disposed by the initial connect.
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        await DisposeConnectionInternalsAsync().ConfigureAwait(false);

        for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(2, attempt), 30));
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            CredentialMaterial? credential = null;
            var jumpChain = new List<JumpHopContext>();
            try
            {
                _logger.LogInformation("Reconnect attempt {Attempt}…", attempt + 1);
                if (_ctx.RefreshCredentialsAsync is not null)
                {
                    var fresh = await _ctx.RefreshCredentialsAsync(ct).ConfigureAwait(false);
                    credential = fresh.Credential;
                    jumpChain  = [.. fresh.JumpChain];
                }

                await ConnectCoreAsync(credential, jumpChain, ct).ConfigureAwait(false);
                SetState(SessionState.Connected);
                _logger.LogInformation("Reconnected successfully after {Attempt} attempt(s).", attempt + 1);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnect attempt {Attempt} failed; retrying in {Delay}s.",
                    attempt + 1, delay.TotalSeconds);
                await DisposeConnectionInternalsAsync().ConfigureAwait(false);
            }
            finally
            {
                credential?.Dispose();
                foreach (var hop in jumpChain)
                    hop.Credential?.Dispose();
            }
        }
    }

    /// <summary>
    /// Tears down the terminal, shell, tunnels, SFTP, jump chain, and primary client, then nulls the
    /// corresponding fields so a subsequent <see cref="ConnectCoreAsync"/> call (from <see cref="ReconnectLoopAsync"/>)
    /// starts from a clean slate. Does not touch <see cref="_state"/> or reconnect bookkeeping.
    /// </summary>
    private async Task DisposeConnectionInternalsAsync()
    {
        (_terminal as IDisposable)?.Dispose();
        _shell?.Dispose();
        _x11Forwarder?.Dispose();
        if (_tunnelManager is not null)
            await _tunnelManager.DisposeAsync().ConfigureAwait(false);
        _sftp?.Dispose();
        DisconnectJumpChain();
        DisposeJumpChain();
        _client?.Dispose();

        _terminal = null;
        _shell = null;
        _x11Forwarder = null;
        _sftp = null;
        _transfer = null;
        _tunnelManager = null;
        _client = null;
    }

    /// <summary>
    /// Cancels any in-flight reconnect loop and closes the connection (shell, SFTP, tunnels, jump chain,
    /// primary client). Safe to call regardless of current <see cref="State"/>.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Closing);
        if (_reconnectCts is not null)
        {
            _reconnectCts.Cancel();
            if (_reconnectTask is not null)
                try { await _reconnectTask.ConfigureAwait(false); } catch { /* already logged inside the loop */ }
            _reconnectCts.Dispose();
            _reconnectCts = null;
        }
        try
        {
            (_terminal as IDisposable)?.Dispose();
            _shell?.Dispose();
            _x11Forwarder?.Dispose();
            if (_tunnelManager is not null)
                await _tunnelManager.DisposeAsync().ConfigureAwait(false);
            if (_sftp?.IsConnected == true)
                await Task.Run(() => _sftp.Disconnect(), ct).ConfigureAwait(false);
            if (_client?.IsConnected == true)
                await Task.Run(() => _client.Disconnect(), ct).ConfigureAwait(false);
            DisconnectJumpChain();
        }
        finally
        {
            SetState(SessionState.Closed);
        }
    }

    /// <summary>Disconnects if not already closed/faulted, then disposes all owned resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_state is not (SessionState.Closed or SessionState.Faulted))
            await DisconnectAsync().ConfigureAwait(false);

        if (_tunnelManager is not null)
            await _tunnelManager.DisposeAsync().ConfigureAwait(false);
        (_terminal as IDisposable)?.Dispose();
        _x11Forwarder?.Dispose();
        _sftp?.Dispose();
        _client?.Dispose();
        DisposeJumpChain();
    }

    private void DisconnectJumpChain()
    {
        for (var i = _jumpForwards.Count - 1; i >= 0; i--)
        {
            try { _jumpForwards[i].Stop(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping jump hop forward."); }
        }
        for (var i = _jumpClients.Count - 1; i >= 0; i--)
        {
            try
            {
                if (_jumpClients[i].IsConnected)
                    _jumpClients[i].Disconnect();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting jump hop."); }
        }
    }

    private void DisposeJumpChain()
    {
        foreach (var forward in _jumpForwards)
            forward.Dispose();
        foreach (var client in _jumpClients)
            client.Dispose();
        _jumpForwards.Clear();
        _jumpClients.Clear();
    }

    /// <summary>
    /// SSH.NET's unexpected-disconnect callback. Only starts the reconnect loop when currently
    /// <see cref="SessionState.Connected"/> — if a disconnect/close/reconnect is already underway, this fires
    /// spuriously (e.g. from tearing down the old client) and must be ignored to avoid overlapping reconnect loops.
    /// </summary>
    private void OnClientError(object? sender, Renci.SshNet.Common.ExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "SSH error occurred.");
        if (_state != SessionState.Connected) return; // already reconnecting/closing/closed — ignore

        _reconnectCts = new CancellationTokenSource();
        SetState(SessionState.Reconnecting);
        _reconnectTask = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token));
    }

    private void SetState(SessionState next)
    {
        var prev = _state;
        _state = next;
        if (prev != next)
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs { Previous = prev, Current = next });
    }

    /// <summary>
    /// Wires <paramref name="client"/>'s host-key callback to <see cref="SessionContext.HostKeyPolicy"/>, evaluated
    /// against <paramref name="realHost"/>/<paramref name="realPort"/> — the actual hop or target address — rather
    /// than the TCP endpoint the client connects to, which for jump-chained hops is a local loopback forward.
    /// Using the real address ensures trust-on-first-use fingerprints are recorded against the right identity.
    /// </summary>
    private void WireHostKeyVerification(SshClient client, string realHost, int realPort, CancellationToken ct)
    {
        // Event fires synchronously during Connect(); GetAwaiter().GetResult() is intentional —
        // the callback must be synchronous. realHost/realPort are always the actual hop/target
        // address, never a jump-chain loopback endpoint, so TOFU keys correctly.
        client.HostKeyReceived += (_, e) =>
        {
            var keyInfo = new HostKeyInfo
            {
                Host              = realHost,
                Port              = realPort,
                Algorithm         = e.HostKeyName,
                FingerprintSha256 = Convert.ToBase64String(e.FingerPrint),
                KeyBytes          = e.HostKey,
            };
            var decision = _ctx.HostKeyPolicy.EvaluateAsync(keyInfo, ct).GetAwaiter().GetResult();
            e.CanTrust = decision == HostKeyDecision.Trust;
            if (!e.CanTrust)
                _logger.LogWarning("Host key rejected for {Host}:{Port} — key changed or policy rejected.", realHost, realPort);
        };
    }

    /// <summary>
    /// Builds the ordered set of auth methods SSH.NET will try: key file path, in-memory key bytes,
    /// then password/keyboard-interactive — the latter two only offered when <paramref name="cred"/> has a secret.
    /// </summary>
    private static IEnumerable<AuthenticationMethod> BuildAuthMethods(
        string username, CredentialMaterial? cred, Dictionary<string, JsonElement> settings)
    {
        // 1. Key file path (from profile settings or credential material)
        var keyPath = GetSetting(settings, "privateKeyPath") ?? cred?.PrivateKeyPath;
        if (keyPath is not null && File.Exists(keyPath))
        {
            PrivateKeyFile keyFile;
            if (cred?.Secret is { Length: > 0 } passphrase)
                keyFile = new PrivateKeyFile(keyPath, Encoding.UTF8.GetString(passphrase));
            else
                keyFile = new PrivateKeyFile(keyPath);
            yield return new PrivateKeyAuthenticationMethod(username, keyFile);
        }

        // 2. In-memory private key bytes from credential provider
        if (cred?.PrivateKey is { Length: > 0 } keyBytes)
        {
            using var ms = new MemoryStream(keyBytes);
            var keyFile = new PrivateKeyFile(ms);
            yield return new PrivateKeyAuthenticationMethod(username, keyFile);
        }

        // 3 & 4. Password + keyboard-interactive — both only when we have a secret.
        // KBI is only useful when we have something to respond with; adding it unconditionally
        // causes SSH.NET to exhaust it against servers that don't advertise keyboard-interactive.
        if (cred?.Secret is { Length: > 0 } secret)
        {
            var secretStr = Encoding.UTF8.GetString(secret);
            yield return new PasswordAuthenticationMethod(username, secretStr);

            var kbi = new KeyboardInteractiveAuthenticationMethod(username);
            kbi.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    if (!prompt.IsEchoed)
                        prompt.Response = secretStr;
            };
            yield return kbi;
        }
    }

    private static string? GetSetting(Dictionary<string, JsonElement> settings, string key) =>
        settings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? GetIntSetting(Dictionary<string, JsonElement> settings, string key) =>
        settings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static bool GetBoolSetting(Dictionary<string, JsonElement> settings, string key) =>
        settings.TryGetValue(key, out var v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        v.GetBoolean();
}

/// <summary>A user-facing connect failure with a short, friendly message; the original low-level exception is preserved as <see cref="Exception.InnerException"/>.</summary>
public sealed class SshConnectException(string message, Exception innerException) : Exception(message, innerException);
