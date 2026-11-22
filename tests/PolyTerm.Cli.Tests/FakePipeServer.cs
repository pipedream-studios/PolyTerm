using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PolyTerm.Cli.Tests;

/// <summary>
/// A single-client fake of PolyTerm.App's <c>McpBridgeService</c> pipe: accepts one connection and
/// answers each newline-delimited JSON request with whatever <c>handle</c> returns, using the bridge's
/// no-BOM UTF-8 framing.
/// <para>
/// <see cref="DisposeAsync"/> cancels the serve loop, waits for it to exit, and disposes the pipe.
/// On macOS/Linux the pipe is a <c>CoreFxPipe_&lt;name&gt;</c> socket file in the temp directory that
/// is only removed on dispose, so a server that outlives the test host leaks the file (issue #226).
/// </para>
/// </summary>
internal sealed class FakePipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cts;
    private readonly Task _serveTask;

    private FakePipeServer(Func<JsonElement, string> handle, CancellationToken ct)
    {
        // Short prefix + truncated GUID (not the full 32 hex chars) - macOS CI runners use a $TMPDIR
        // long enough that a full GUID pushes the socket path past .NET's 104-byte
        // UnixDomainSocketEndPoint limit, failing with ArgumentOutOfRangeException before connecting.
        PipeName = $"pt-{Guid.NewGuid():N}"[..15];
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // CancellationToken.None: the loop always runs, so DisposeAsync has one task to await either way.
        _serveTask = Task.Run(() => ServeAsync(handle, _cts.Token), CancellationToken.None);
    }

    public string PipeName { get; }

    /// <summary>Starts serving. <paramref name="handle"/> gets each request's root element and returns the full response line.</summary>
    public static FakePipeServer Start(Func<JsonElement, string> handle, CancellationToken ct = default) => new(handle, ct);

    private async Task ServeAsync(Func<JsonElement, string> handle, CancellationToken ct)
    {
        try
        {
            await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using var reader = new StreamReader(_pipe, noBomUtf8, leaveOpen: true);
            await using var writer = new StreamWriter(_pipe, noBomUtf8, leaveOpen: true) { AutoFlush = true };

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break; // client disconnected

                string response;
                using (var doc = JsonDocument.Parse(line))
                    response = handle(doc.RootElement);
                await writer.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* teardown */ }
        catch (IOException) { /* client went away mid-write */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _serveTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
