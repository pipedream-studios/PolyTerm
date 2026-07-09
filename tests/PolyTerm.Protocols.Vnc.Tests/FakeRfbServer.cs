using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PolyTerm.Protocols.Vnc.Tests;

/// <summary>
/// A minimal, hand-rolled RFB (RFB 3.8) server — just enough of the real wire protocol for
/// <c>MarcusW.VncClient</c>'s real client implementation to successfully negotiate a connection and
/// receive one crafted framebuffer update, so tests can verify PolyTerm's actual decoded output
/// against known input bytes without needing a real VNC server. Same fake-server technique already
/// used for Telnet's real-server verification, applied to RFB's specific handshake.
/// Security type "None", one rectangle, Raw encoding only — nothing else is implemented.
/// </summary>
internal sealed class FakeRfbServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _pixelRgb; // one R,G,B triple, repeated for every pixel in the crafted rectangle
    private Task? _acceptTask;

    public int Port { get; }

    public FakeRfbServer(int width, int height, (byte R, byte G, byte B) pixel)
    {
        _width = width;
        _height = height;
        _pixelRgb = [pixel.R, pixel.G, pixel.B];

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start(CancellationToken ct) => _acceptTask = RunAsync(ct);

    private async Task RunAsync(CancellationToken ct)
    {
        using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        using var stream = client.GetStream();

        // 1. ProtocolVersion handshake
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), ct).ConfigureAwait(false);
        await ReadExactAsync(stream, 12, ct).ConfigureAwait(false); // client's version string, unused

        // 2. Security: offer only "None" (type 1)
        await stream.WriteAsync(new byte[] { 1, 1 }, ct).ConfigureAwait(false);
        await ReadExactAsync(stream, 1, ct).ConfigureAwait(false); // client's chosen type, unused

        // 3. SecurityResult — required even for "None" under RFB 3.8
        await stream.WriteAsync(new byte[] { 0, 0, 0, 0 }, ct).ConfigureAwait(false);

        // 4. ClientInit
        await ReadExactAsync(stream, 1, ct).ConfigureAwait(false); // shared-flag, unused

        // 5. ServerInit — standard 32bpp true-color format: R at bit-shift 16, G at 8, B at 0,
        // little-endian, no real alpha (depth 24, the classic "default" VNC server pixel format).
        var serverInit = new List<byte>();
        serverInit.AddRange(BigEndianU16((ushort)_width));
        serverInit.AddRange(BigEndianU16((ushort)_height));
        serverInit.Add(32);              // bits-per-pixel
        serverInit.Add(24);              // depth
        serverInit.Add(0);               // big-endian-flag = false
        serverInit.Add(1);               // true-color-flag = true
        serverInit.AddRange(BigEndianU16(255)); // red-max
        serverInit.AddRange(BigEndianU16(255)); // green-max
        serverInit.AddRange(BigEndianU16(255)); // blue-max
        serverInit.Add(16);              // red-shift
        serverInit.Add(8);               // green-shift
        serverInit.Add(0);               // blue-shift
        serverInit.AddRange(new byte[] { 0, 0, 0 }); // padding
        var name = Encoding.ASCII.GetBytes("test");
        serverInit.AddRange(BigEndianU32((uint)name.Length));
        serverInit.AddRange(name);
        await stream.WriteAsync(serverInit.ToArray(), ct).ConfigureAwait(false);

        // 6. Wait for the client's post-connect message burst (SetPixelFormat/SetEncodings/
        // FramebufferUpdateRequest) — content is ignored; Raw encoding is a mandatory baseline
        // every RFB client must support, so replying with it is always valid regardless of what
        // encodings the client declared support for.
        var drain = new byte[4096];
        await stream.ReadAsync(drain, ct).ConfigureAwait(false);

        // 7. FramebufferUpdate: one rectangle, Raw encoding, every pixel the same known R/G/B.
        var update = new List<byte> { 0, 0 }; // message-type=0, padding
        update.AddRange(BigEndianU16(1)); // number-of-rectangles
        update.AddRange(BigEndianU16(0)); // x
        update.AddRange(BigEndianU16(0)); // y
        update.AddRange(BigEndianU16((ushort)_width));
        update.AddRange(BigEndianU16((ushort)_height));
        update.AddRange(BigEndianU32(0)); // encoding-type = Raw

        for (var i = 0; i < _width * _height; i++)
        {
            // Server's declared format: R<<16 | G<<8 | B, written little-endian (byte0=B,1=G,2=R,3=pad).
            update.Add(_pixelRgb[2]); // B
            update.Add(_pixelRgb[1]); // G
            update.Add(_pixelRgb[0]); // R
            update.Add(0);           // padding byte (unused in a 24-depth/32bpp format)
        }

        await stream.WriteAsync(update.ToArray(), ct).ConfigureAwait(false);

        // Keep the connection open briefly so the client has time to process the update before the
        // test disposes everything — the client-side read loop runs on its own background thread.
        await Task.Delay(500, ct).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("Client closed the connection during handshake.");
            read += n;
        }
    }

    private static byte[] BigEndianU16(ushort value) => [(byte)(value >> 8), (byte)value];

    private static byte[] BigEndianU32(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { } // listener stopped mid-accept/read — expected on teardown
        }
    }
}
