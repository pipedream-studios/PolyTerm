using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.RdpFreerdp.Tests;

/// <summary>
/// That a profile's "Share clipboard" setting (issue #390) reaches FreeRDP, seen from the server's end:
/// a stand-in server on loopback picks plain RDP security, so the client's list of channels arrives
/// unencrypted, then hangs up. No RDP host needed.
/// </summary>
public sealed class RdpShareClipboardTests
{
    private sealed class AlwaysTrustHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            Task.FromResult(HostKeyDecision.Trust);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("""{ "shareClipboard": false }""", false)]
    public async Task TheClipboardChannelIsRequestedOnlyWhenSharingIsOn(string settingsJson, bool expected)
    {
        if (!NativeMethods.TryLoadLibrary())
            Assert.Skip("The native RDP shim isn't built for this platform (see native/PolyTerm-rdp-shim).");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var channelList = ReadChannelListAsync(listener, timeout.Token);

        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsJson)!;
        // A drive share, so there's always one channel whose presence proves the list was read.
        settings["driveShares"] = JsonSerializer.SerializeToElement(new[] { new { name = "tmp", path = Path.GetTempPath() } });

        using var credential = new CredentialMaterial { Username = "user", Secret = "not-a-real-password"u8.ToArray() };
        await using var session = new RdpFreerdpProtocolDescriptor(NullLoggerFactory.Instance).CreateSession(new SessionContext
        {
            SessionId = Guid.NewGuid(),
            Profile = new ConnectionProfile
            {
                Name = "rdp-share-clipboard-test",
                ProtocolId = "rdp",
                Host = IPAddress.Loopback.ToString(),
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                Settings = settings,
            },
            Credential = credential,
            HostKeyPolicy = new AlwaysTrustHostKeyPolicy(),
        });

        var connect = () => session.ConnectAsync(timeout.Token);
        // The stand-in hangs up once it has what it needs.
        var connecting = connect.Should().ThrowAsync<InvalidOperationException>();

        var names = Encoding.Latin1.GetString(await channelList);
        await connecting;

        names.Should().Contain("rdpdr\0");
        names.Contains("cliprdr\0", StringComparison.Ordinal).Should().Be(expected);
    }

    /// <summary>Plays the server up to the client's MCS Connect Initial, which names the static channels it wants.</summary>
    private static async Task<byte[]> ReadChannelListAsync(TcpListener listener, CancellationToken ct)
    {
        // X.224 Connection Confirm with an RDP Negotiation Response selecting PROTOCOL_RDP (MS-RDPBCGR 2.2.1.2).
        byte[] confirm = [3, 0, 0, 19, 14, 0xD0, 0, 0, 0x12, 0x34, 0, 2, 0, 8, 0, 0, 0, 0, 0];

        // FreeRDP may reconnect to retry with another security protocol, so keep serving until one gets that far.
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            try
            {
                await ReadPacketAsync(stream, ct); // Connection Request
                await stream.WriteAsync(confirm, ct);
                var connectInitial = await ReadPacketAsync(stream, ct);
                // Before hanging up, so that FreeRDP's retry is refused rather than left waiting.
                listener.Stop();
                return connectInitial;
            }
            catch (IOException)
            {
                // The client dropped this attempt.
            }
        }
    }

    /// <summary>One TPKT packet: a 4-byte header whose last two bytes are the total length.</summary>
    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var body = new byte[((header[2] << 8) | header[3]) - header.Length];
        await stream.ReadExactlyAsync(body, ct);
        return body;
    }
}
