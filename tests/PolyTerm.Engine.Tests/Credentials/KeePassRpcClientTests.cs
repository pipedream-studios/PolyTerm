using System.Net;
using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine.Credentials;
using Xunit;

namespace PolyTerm.Engine.Tests.Credentials;

/// <summary>
/// Drives <see cref="KeePassRpcClient"/> against a real local WebSocket server (a hand-rolled fake
/// KeePassRPC, not a mock) — matching this repo's general testing convention (see
/// <c>BitwardenApiClientTests</c>' real-<c>HttpListener</c>-backed fake). The fake server independently
/// implements the SRP-6 handshake and encrypted JSON-RPC envelope over its own connection state (mirroring
/// real KeePassRPC's per-connection <c>KeePassRPCClientConnection</c> object), reusing only the
/// protocol-constant crypto primitives from <see cref="KeePassRpcClient"/> itself (SRP group parameters,
/// hash/hex-format helpers, the Rijndael+SHA1-MAC envelope) via <c>InternalsVisibleTo</c> — those are
/// fixed by the real KeePassRPC protocol, not implementation choices, so both sides being built on them
/// is expected, not test-against-itself. What this genuinely exercises independently is the message
/// *sequence* and JSON *shapes* at each stage. What it can't substitute for: whether this wire format
/// (verified against <c>kee-org/keepassrpc</c>'s C# source — see issue #120's plan) matches a real,
/// running KeePass2+KeePassRPC installation.
/// </summary>
public sealed class KeePassRpcClientTests
{
    private const string PairingCode = "TESTCODE1234";

    [Fact]
    public async Task Pairing_WithCorrectCode_Succeeds_AndDerivesAWorkingSharedKey()
    {
        using var server = new FakeKeePassRpcServer(PairingCode);
        await using var client = new KeePassRpcClient(server.Port, NullLogger<KeePassRpcClient>.Instance);
        await client.ConnectAsync();

        var pending = await client.BeginPairingAsync("PolyTerm", "PolyTerm", "test client");
        var result = await client.CompletePairingAsync(pending, PairingCode);

        result.Success.Should().BeTrue();
        result.SharedKeyHex.Should().NotBeNullOrEmpty();
        server.PairedClientId.Should().Be("PolyTerm");
        server.PairedSharedKeyHex.Should().Be(result.SharedKeyHex);
    }

    [Fact]
    public async Task Pairing_WithWrongCode_Fails_AndDoesNotPersistAnything()
    {
        using var server = new FakeKeePassRpcServer(PairingCode);
        await using var client = new KeePassRpcClient(server.Port, NullLogger<KeePassRpcClient>.Instance);
        await client.ConnectAsync();

        var pending = await client.BeginPairingAsync("PolyTerm", "PolyTerm", "test client");
        var result = await client.CompletePairingAsync(pending, "totally wrong code");

        result.Success.Should().BeFalse();
        result.SharedKeyHex.Should().BeNull();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        server.PairedClientId.Should().BeNull("a failed proof must not leave the server thinking pairing succeeded");
    }

    [Fact]
    public async Task Authenticate_WithThePersistedKeyFromPairing_Succeeds_OnABrandNewConnection()
    {
        using var server = new FakeKeePassRpcServer(PairingCode);
        string sharedKeyHex;
        await using (var pairingClient = new KeePassRpcClient(server.Port, NullLogger<KeePassRpcClient>.Instance))
        {
            await pairingClient.ConnectAsync();
            var pending = await pairingClient.BeginPairingAsync("PolyTerm", "PolyTerm", "test client");
            var result = await pairingClient.CompletePairingAsync(pending, PairingCode);
            sharedKeyHex = result.SharedKeyHex!;
        }

        // A fresh connection, exactly as a later app launch would do it - re-auth must not need the
        // pairing code again, only the shared key persisted from the first pairing.
        await using var client = new KeePassRpcClient(server.Port, NullLogger<KeePassRpcClient>.Instance);
        await client.ConnectAsync();
        var authorised = await client.AuthenticateAsync("PolyTerm", sharedKeyHex);

        authorised.Should().BeTrue();
    }

    [Fact]
    public async Task Authenticate_WithAWrongKey_Fails()
    {
        using var server = new FakeKeePassRpcServer(PairingCode);
        await using (var pairingClient = new KeePassRpcClient(server.Port, NullLogger<KeePassRpcClient>.Instance))
        {
            await pairingClient.ConnectAsync();
            var pending = await pairingClient.BeginPairingAsync("PolyTerm", "PolyTerm", "test client");
            await pairingClient.CompletePairingAsync(pending, PairingCode);
        }

        await using var client = new KeePassRpcClient(server.Port, NullLogger<KeePassRpcClient>.Instance);
        await client.ConnectAsync();
        var authorised = await client.AuthenticateAsync("PolyTerm", new string('0', 64));

        authorised.Should().BeFalse();
    }

    [Fact]
    public async Task BrowseAllEntriesAsync_ReturnsEveryEntryTheFakeServerHolds()
    {
        using var server = new FakeKeePassRpcServer(PairingCode);
        server.Entries.Add(("uuid-1", "GitHub", "octocat", "hunter2"));
        server.Entries.Add(("uuid-2", "No Password Entry", "someone", null));

        await using var client = new KeePassRpcClient(server.Port, NullLogger<KeePassRpcClient>.Instance);
        await client.ConnectAsync();
        var pending = await client.BeginPairingAsync("PolyTerm", "PolyTerm", "test client");
        var pairResult = await client.CompletePairingAsync(pending, PairingCode);

        var entries = await client.BrowseAllEntriesAsync(pairResult.SharedKeyHex!);

        entries.Should().HaveCount(2);
        entries.Should().ContainSingle(e => e.Uuid == "uuid-1" && e.Title == "GitHub" && e.Username == "octocat");
        entries.Should().ContainSingle(e => e.Uuid == "uuid-2" && e.Username == "someone");
    }

    [Fact]
    public async Task ResolveEntryAsync_ReturnsTheUsernameAndPassword_ForAMatchingUuid()
    {
        using var server = new FakeKeePassRpcServer(PairingCode);
        server.Entries.Add(("uuid-1", "GitHub", "octocat", "hunter2"));

        await using var client = new KeePassRpcClient(server.Port, NullLogger<KeePassRpcClient>.Instance);
        await client.ConnectAsync();
        var pending = await client.BeginPairingAsync("PolyTerm", "PolyTerm", "test client");
        var pairResult = await client.CompletePairingAsync(pending, PairingCode);

        var resolved = await client.ResolveEntryAsync(pairResult.SharedKeyHex!, "uuid-1");

        resolved.Should().NotBeNull();
        resolved!.Value.Username.Should().Be("octocat");
        resolved.Value.Password.Should().Be("hunter2");
    }

    [Fact]
    public async Task ResolveEntryAsync_ReturnsNull_ForAnUnknownUuid()
    {
        using var server = new FakeKeePassRpcServer(PairingCode);
        server.Entries.Add(("uuid-1", "GitHub", "octocat", "hunter2"));

        await using var client = new KeePassRpcClient(server.Port, NullLogger<KeePassRpcClient>.Instance);
        await client.ConnectAsync();
        var pending = await client.BeginPairingAsync("PolyTerm", "PolyTerm", "test client");
        var pairResult = await client.CompletePairingAsync(pending, PairingCode);

        var resolved = await client.ResolveEntryAsync(pairResult.SharedKeyHex!, "no-such-uuid");

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ConnectAsync_Throws_WhenTheServerRejectsTheOrigin()
    {
        using var server = new FakeKeePassRpcServer(PairingCode) { RejectOrigin = true };
        await using var client = new KeePassRpcClient(server.Port, NullLogger<KeePassRpcClient>.Instance);

        var act = async () => await client.ConnectAsync();

        await act.Should().ThrowAsync<Exception>("a real KeePassRPC install rejects unrecognised Origins the same way, before ever upgrading the connection");
    }
}

/// <summary>
/// Independent fake KeePassRPC server for <see cref="KeePassRpcClientTests"/> — real WebSocket transport
/// via <see cref="HttpListener"/>'s upgrade support, real SRP-6/Rijndael+SHA1-MAC crypto (reusing
/// <see cref="KeePassRpcClient"/>'s protocol-constant primitives via <c>InternalsVisibleTo</c>, not a
/// second hand-rolled implementation of the same fixed constants), independently implementing the
/// message sequence real KeePassRPC's <c>KeePassRPCClientConnection</c> drives server-side.
/// </summary>
file sealed class FakeKeePassRpcServer : IDisposable
{
    private readonly string _pairingCode;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private const string RootGroupUuid = "root-group";
    private static readonly string[] s_keysDoNotMatch = ["Keys do not match"];
    private static readonly string[] s_storedKeyNotFound = ["Stored key not found"];

    public int Port { get; }
    public bool RejectOrigin { get; set; }
    public string? PairedClientId { get; private set; }
    public string? PairedSharedKeyHex { get; private set; }
    public List<(string Uuid, string Title, string Username, string? Password)> Entries { get; } = [];

    public FakeKeePassRpcServer(string pairingCode)
    {
        _pairingCode = pairingCode;
        Port = GetFreeTcpPort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or HttpListenerException)
            {
                return;
            }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            var origin = ctx.Request.Headers["Origin"];
            if (RejectOrigin || string.IsNullOrEmpty(origin) || !origin.StartsWith("polyterm://", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                continue;
            }

            var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
            _ = Task.Run(() => HandleConnectionAsync(wsCtx.WebSocket));
        }
    }

    private async Task HandleConnectionAsync(WebSocket ws)
    {
        // Per-connection SRP handshake state, mirroring real KeePassRPC's per-connection object.
        string? astr = null;
        BigInteger a = default, v = default, bPriv = default, bPub = default;
        string? salt = null, bstr = null, pendingClientId = null, pendingSc = null;

        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var messageJson = await ReceiveTextAsync(ws, _cts.Token).ConfigureAwait(false);
                if (messageJson is null) return;

                using var doc = JsonDocument.Parse(messageJson);
                var root = doc.RootElement;
                var protocol = root.GetProperty("protocol").GetString();

                if (protocol == "jsonrpc")
                {
                    await HandleJsonRpcAsync(ws, root, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                if (protocol != "setup") continue;

                if (root.TryGetProperty("srp", out var srp) && srp.ValueKind == JsonValueKind.Object
                    && srp.TryGetProperty("stage", out var stageEl))
                {
                    var stage = stageEl.GetString();
                    if (stage == "identifyToServer")
                    {
                        pendingClientId = srp.GetProperty("I").GetString();
                        astr = srp.GetProperty("A").GetString();
                        a = KeePassRpcClient.ParseKprpcHex(astr!);

                        salt = new BigInteger(RandomNumberGenerator.GetBytes(32), isUnsigned: true, isBigEndian: true).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var x = KeePassRpcClient.HashToBigInteger(salt + _pairingCode);
                        v = BigInteger.ModPow(KeePassRpcClient.G, x, KeePassRpcClient.N);
                        bPriv = KeePassRpcClient.RandomBigInteger(32);
                        bPub = (KeePassRpcClient.K * v + BigInteger.ModPow(KeePassRpcClient.G, bPriv, KeePassRpcClient.N)) % KeePassRpcClient.N;
                        bstr = KeePassRpcClient.ToKprpcHex(bPub);

                        await SendTextAsync(ws, JsonSerializer.Serialize(new
                        {
                            protocol = "setup", version = 0,
                            srp = new { stage = "identifyToClient", s = salt, B = bstr },
                        }), _cts.Token).ConfigureAwait(false);
                    }
                    else if (stage == "proofToServer" && salt is not null)
                    {
                        var mClient = srp.GetProperty("M").GetString();
                        var u = KeePassRpcClient.HashToBigInteger(astr + bstr);
                        var avu = a * BigInteger.ModPow(v, u, KeePassRpcClient.N);
                        var s = BigInteger.ModPow(avu, bPriv, KeePassRpcClient.N);
                        var sstr = KeePassRpcClient.ToKprpcHex(s);
                        var expectedM = KeePassRpcClient.HashHex(astr + bstr + sstr);

                        if (expectedM != mClient)
                        {
                            await SendTextAsync(ws, JsonSerializer.Serialize(new
                            {
                                protocol = "setup", version = 0,
                                error = new { code = 1, messageParams = s_keysDoNotMatch },
                            }), _cts.Token).ConfigureAwait(false);
                            continue;
                        }

                        var m2 = KeePassRpcClient.HashHex(astr + expectedM + sstr);
                        PairedClientId = pendingClientId;
                        PairedSharedKeyHex = KeePassRpcClient.HashHex(sstr);

                        await SendTextAsync(ws, JsonSerializer.Serialize(new
                        {
                            protocol = "setup", version = 0,
                            srp = new { stage = "proofToClient", M2 = m2 },
                        }), _cts.Token).ConfigureAwait(false);
                    }
                }
                else if (root.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object)
                {
                    if (key.TryGetProperty("username", out var userEl))
                    {
                        var username = userEl.GetString();
                        if (username != PairedClientId || PairedSharedKeyHex is null)
                        {
                            await SendTextAsync(ws, JsonSerializer.Serialize(new
                            {
                                protocol = "setup", version = 0,
                                error = new { code = 2, messageParams = s_storedKeyNotFound },
                            }), _cts.Token).ConfigureAwait(false);
                            continue;
                        }

                        pendingSc = new BigInteger(RandomNumberGenerator.GetBytes(32), isUnsigned: true, isBigEndian: true).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        await SendTextAsync(ws, JsonSerializer.Serialize(new
                        {
                            protocol = "setup", version = 0,
                            key = new { sc = pendingSc },
                        }), _cts.Token).ConfigureAwait(false);
                    }
                    else if (key.TryGetProperty("cc", out var ccEl) && key.TryGetProperty("cr", out var crEl) && pendingSc is not null)
                    {
                        var cc = ccEl.GetString();
                        var cr = crEl.GetString();
                        var expectedCr = KeePassRpcClient.HashHex("1" + PairedSharedKeyHex + pendingSc + cc);
                        if (expectedCr != cr)
                        {
                            await SendTextAsync(ws, JsonSerializer.Serialize(new
                            {
                                protocol = "setup", version = 0,
                                error = new { code = 3, messageParams = s_keysDoNotMatch },
                            }), _cts.Token).ConfigureAwait(false);
                            continue;
                        }

                        var sr = KeePassRpcClient.HashHex("0" + PairedSharedKeyHex + pendingSc + cc);
                        await SendTextAsync(ws, JsonSerializer.Serialize(new
                        {
                            protocol = "setup", version = 0,
                            key = new { sr },
                        }), _cts.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Expected: KeePassRpcClient.DisposeAsync() deliberately Abort()s rather than performing a
            // graceful close handshake (see that method's own comment), so the server side's pending
            // ReceiveAsync routinely observes the connection disappearing mid-read - not an error.
        }
        catch (Exception ex)
        {
            // A per-connection background handler (started via Task.Run) - any *unexpected* exception
            // here would otherwise vanish silently as an unobserved task fault instead of failing the
            // test loudly.
            Console.Error.WriteLine($"[FakeKeePassRpcServer] connection handler faulted: {ex}");
        }
    }

    private async Task HandleJsonRpcAsync(WebSocket ws, JsonElement root, CancellationToken ct)
    {
        var jsonrpcEl = root.GetProperty("jsonrpc");
        var container = new KeePassRpcClient.KprpcJsonRpcContainer
        {
            Message = jsonrpcEl.GetProperty("message").GetString(),
            Iv = jsonrpcEl.GetProperty("iv").GetString(),
            Hmac = jsonrpcEl.GetProperty("hmac").GetString(),
        };

        var plaintext = KeePassRpcClient.Decrypt(container, PairedSharedKeyHex!)
            ?? throw new InvalidOperationException("Fake server could not decrypt an incoming JSON-RPC message.");

        using var reqDoc = JsonDocument.Parse(plaintext);
        var reqRoot = reqDoc.RootElement;
        var id = reqRoot.GetProperty("id").GetInt32();
        var method = reqRoot.GetProperty("method").GetString();

        object result = method switch
        {
            "GetRoot" => new { UniqueID = RootGroupUuid },
            "GetChildGroups" => Array.Empty<object>(),
            "GetAllChildEntries" => Entries.Select(e => new
            {
                UniqueID = e.Uuid,
                Title = e.Title,
                UsernameValue = e.Username,
                UsernameName = "username",
                FormFieldList = e.Password is not null
                    ? new object[] { new { Type = 3, Value = e.Password } } // FFTpassword = 3
                    : Array.Empty<object>(),
            }).ToArray(),
            _ => throw new InvalidOperationException($"Fake server has no handler for method '{method}'."),
        };

        var responseJson = JsonSerializer.Serialize(new { id, result });
        var responseContainer = KeePassRpcClient.Encrypt(responseJson, PairedSharedKeyHex!);
        await SendTextAsync(ws, JsonSerializer.Serialize(new
        {
            protocol = "jsonrpc", version = 0,
            jsonrpc = new { message = responseContainer.Message, iv = responseContainer.Iv, hmac = responseContainer.Hmac },
        }), ct).ConfigureAwait(false);
    }

    private static async Task SendTextAsync(WebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _listener.Close();
        _cts.Dispose();
    }
}
