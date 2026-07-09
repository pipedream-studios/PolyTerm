using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>
/// A real, ad-hoc Windows OpenSSH server (<c>sshd.exe</c>) — not a hand-rolled fake. SSH-2 requires
/// real Diffie-Hellman key exchange and host-key signing; hand-rolling that (the way
/// <c>FakeRfbServer</c> hand-rolls VNC's much simpler handshake) would be a large, high-risk
/// undertaking on its own. Windows ships an OpenSSH Server binary at
/// <c>C:\Windows\System32\OpenSSH\sshd.exe</c> that runs perfectly well ad-hoc — no service
/// registration, no admin rights, no Docker — confirmed empirically before building this fixture:
/// see docs/testing.md's SSH section. Generates a throwaway ed25519 host key and client key pair per
/// instance (via <c>ssh-keygen.exe</c>, the same tool alongside <c>sshd.exe</c>), binds to a random
/// loopback port, and requires <see cref="PolyTerm.Protocols.Ssh"/>'s own key-file auth path
/// (<c>ConnectionProfile.Settings["privateKeyPath"]</c>) — no vault/credential involvement.
/// </summary>
internal sealed class FakeSshServer : IAsyncDisposable
{
    private static readonly string OpenSshDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH");
    private static readonly string SshdPath = Path.Combine(OpenSshDir, "sshd.exe");
    private static readonly string SshKeygenPath = Path.Combine(OpenSshDir, "ssh-keygen.exe");
    private static readonly string SftpServerPath = Path.Combine(OpenSshDir, "sftp-server.exe");

    private readonly string _workDir;
    private string _configPath = "";
    private Process? _process;

    public int Port { get; }
    public string ClientPrivateKeyPath { get; }
    public string Username { get; } = Environment.UserName;

    private FakeSshServer(int port, string workDir)
    {
        Port = port;
        _workDir = workDir;
        ClientPrivateKeyPath = Path.Combine(workDir, "client_key");
    }

    /// <summary>Generates throwaway keys, writes a minimal config, and starts <c>sshd.exe</c>. Throws if Windows OpenSSH Server isn't installed at the well-known path.</summary>
    public static async Task<FakeSshServer> StartAsync(CancellationToken ct = default)
    {
        if (!File.Exists(SshdPath))
            throw new InvalidOperationException(
                $"Windows OpenSSH Server not found at '{SshdPath}'. These tests need the optional " +
                "\"OpenSSH Server\" Windows feature installed (Settings > Apps > Optional Features) " +
                "— see docs/testing.md's SSH section.");

        var workDir = Path.Combine(Path.GetTempPath(), $"polyterm-sshd-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(workDir);

        var hostKeyPath = Path.Combine(workDir, "host_key");
        var clientKeyPath = Path.Combine(workDir, "client_key");
        await RunKeygenAsync(hostKeyPath, ct).ConfigureAwait(false);
        await RunKeygenAsync(clientKeyPath, ct).ConfigureAwait(false);

        var port = GetFreePort();
        var configPath = Path.Combine(workDir, "sshd_config");
        await File.WriteAllTextAsync(configPath, $"""
            Port {port}
            ListenAddress 127.0.0.1
            HostKey {ToForwardSlashes(hostKeyPath)}
            AuthorizedKeysFile {ToForwardSlashes(clientKeyPath)}.pub
            PasswordAuthentication no
            PubkeyAuthentication yes
            StrictModes no
            Subsystem sftp {ToForwardSlashes(SftpServerPath)}

            """, ct).ConfigureAwait(false);

        var server = new FakeSshServer(port, workDir) { _configPath = configPath };
        server._process = StartSshdProcess(configPath);

        // sshd needs a moment to bind its listening socket before a client can connect.
        await WaitForPortOpenAsync(port, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

        return server;
    }

    private static Process StartSshdProcess(string configPath) =>
        Process.Start(new ProcessStartInfo(SshdPath, $"-f \"{configPath}\" -D")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start sshd.exe.");

    /// <summary>
    /// Kills the current <c>sshd.exe</c> process (simulating a dropped connection — e.g. the server
    /// crashing or the network dying) without deleting the host key/config, so
    /// <see cref="RestartOnSamePortAsync"/> can bring the exact same server identity back on the exact
    /// same port. A client mid-session sees this as an unexpected disconnect, the same as any real
    /// connectivity loss.
    /// </summary>
    public async Task StopAsync()
    {
        if (_process is not { HasExited: false } p) return;
        try { p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        try { await p.WaitForExitAsync().ConfigureAwait(false); }
        catch { }
        p.Dispose();
        _process = null;
    }

    /// <summary>Starts a fresh <c>sshd.exe</c> using the exact same config/host key/port as the original — for <see cref="SshSession"/>'s auto-reconnect to find a real listener again.</summary>
    public async Task RestartOnSamePortAsync(CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        _process = StartSshdProcess(_configPath);
        await WaitForPortOpenAsync(Port, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
    }

    private static async Task RunKeygenAsync(string keyPath, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo(SshKeygenPath,
            $"-t ed25519 -f \"{keyPath}\" -N \"\" -q")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start ssh-keygen.exe.");
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ssh-keygen.exe exited with code {proc.ExitCode}.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // small race window between releasing this and sshd binding it — acceptable for test purposes
        return port;
    }

    private static async Task WaitForPortOpenAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        throw new TimeoutException($"sshd.exe did not start listening on port {port} within {timeout}.");
    }

    private static string ToForwardSlashes(string path) => path.Replace('\\', '/');

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        try { Directory.Delete(_workDir, recursive: true); }
        catch { } // best-effort cleanup
    }
}
