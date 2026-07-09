using System.Text;
using Renci.SshNet;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Ssh;

/// <summary>
/// Terminal channel backed by an SSH.NET <see cref="ShellStream"/>. The read loop is started lazily on first
/// <see cref="OutputReceived"/> subscription, so no bytes (including the login banner) are lost while the
/// terminal surface is still wiring up.
/// </summary>
internal sealed class SshTerminalChannel : ITerminalChannel, IDisposable
{
    private readonly ShellStream _shell;
    private readonly string? _loginBanner;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readTask;
    private bool _disposed;

    // Passive sinks registered via RegisterOutputSink — fired from ReadLoop alongside OutputReceived,
    // but their registration does NOT start the lazy read loop (unlike OutputReceived.add).
    private Action<ReadOnlyMemory<byte>>? _sinks;

    public string TerminalType { get; }

    // Lazy: ReadLoop starts on first OutputReceived subscription so the surface
    // is ready before bytes flow, avoiding lost initial banner/prompt.
    private EventHandler<ReadOnlyMemory<byte>>? _outputReceived;

    /// <summary>
    /// Subscribing for the first time starts the background read loop (see <see cref="ReadLoop"/>); the loop
    /// keeps running for the lifetime of the channel even if all subscribers later unsubscribe.
    /// </summary>
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived
    {
        add    { _outputReceived += value; _readTask ??= Task.Run(ReadLoop); }
        remove => _outputReceived -= value;
    }

    /// <summary>
    /// Registers a passive sink that receives the same byte chunks as <see cref="OutputReceived"/>, without
    /// starting the read loop — use this to attach a logger/recorder before the terminal surface subscribes.
    /// </summary>
    public void RegisterOutputSink(Action<ReadOnlyMemory<byte>> sink) => _sinks += sink;

    public SshTerminalChannel(ShellStream shell, string terminalType, string? loginBanner = null)
    {
        _shell = shell;
        TerminalType = terminalType;
        _loginBanner = string.IsNullOrWhiteSpace(loginBanner) ? null : loginBanner;
    }

    public async ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await _shell.WriteAsync(data, ct).ConfigureAwait(false);
        await _shell.FlushAsync(ct).ConfigureAwait(false);
    }

    public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        _shell.ChangeWindowSize((uint)cols, (uint)rows, 0u, 0u);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Emits the captured login banner (if any) first, then continuously reads from the shell stream
    /// and fans each chunk out to <see cref="OutputReceived"/> subscribers and registered sinks until
    /// cancelled or the remote side closes the stream.
    /// </summary>
    private async Task ReadLoop()
    {
        if (_loginBanner is not null)
        {
            var normalised = _loginBanner.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var bannerBytes = Encoding.UTF8.GetBytes(normalised).AsMemory();
            _outputReceived?.Invoke(this, bannerBytes);
            _sinks?.Invoke(bannerBytes);
        }

        var buf = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _shell.ReadAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
                if (read == 0) break;
                var chunk = new ReadOnlyMemory<byte>(buf, 0, read);
                _outputReceived?.Invoke(this, chunk);
                _sinks?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* remote side closed */ }
    }

    /// <summary>Stops the read loop and disposes the underlying shell stream. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _shell.Dispose();
    }
}
