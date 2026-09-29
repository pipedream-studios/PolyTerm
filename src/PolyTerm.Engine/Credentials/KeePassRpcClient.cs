using System.Globalization;
using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.Credentials;

/// <summary>Thrown for any KeePassRPC-level failure (protocol error, MAC mismatch, unexpected message shape).</summary>
public sealed class KeePassRpcException(string message) : Exception(message);

/// <summary>Outcome of <see cref="KeePassRpcClient.CompletePairingAsync"/>.</summary>
public sealed record KeePassPairingResult(bool Success, string? SharedKeyHex, string? ErrorMessage);

/// <summary>An entry as returned by KeePassRPC's <c>GetAllChildEntries</c> — password isn't included here (see <see cref="KeePassRpcClient.ResolveEntryAsync"/>).</summary>
public sealed record KeePassLoginEntry(string Uuid, string Title, string? Username);

/// <summary>
/// Talks to a running KeePass2+KeePassRPC instance over its real WebSocket protocol (default
/// <c>ws://localhost:12546</c>) — SRP-6 pairing handshake, persisted-shared-key re-authentication, and
/// encrypted JSON-RPC for entry browsing/resolution. Wire protocol (SRP group parameters, hash/cipher
/// choices, message field names) verified directly against <c>kee-org/keepassrpc</c>'s C# source
/// (<c>SRP.cs</c>, <c>KeePassRPCClient.cs</c>, <c>KeyChallengeResponse.cs</c>), not reverse-engineered —
/// see issue #120's plan for the exact files read.
///
/// <b>Setup requirement</b>: KeePassRPC only accepts WebSocket connections whose <c>Origin</c> header
/// starts with one of its permitted-origins list, which defaults to browser-extension URI schemes only
/// (<c>chrome-extension://</c> etc. — see <c>KeePassRPCExt.DeterminePermittedOrigins</c>). A desktop
/// client like PolyTerm has no such origin by default, so pairing will silently fail (KeePassRPC just
/// never registers the connection) unless the user adds <see cref="OriginHeaderValue"/> to
/// "Allowed Origins" in KeePass's Tools → KeePassRPC Options first.
/// </summary>
public sealed class KeePassRpcClient : IAsyncDisposable
{
    /// <summary>Origin header PolyTerm sends — must be added to KeePassRPC's "Allowed Origins" option before pairing will work.</summary>
    public const string OriginHeaderValue = "polyterm://app";

    // KeePassRPC's SRP-6 group parameters are fixed constants, not the standard RFC 5054 group, and k
    // is a fixed constant too (not H(N,g) as in real SRP-6a) - see SRP.cs's constructor. Both sides
    // must use the exact same values or the handshake silently fails to derive matching keys.
    internal static readonly BigInteger N = ParseKprpcHex(
        "d4c7f8a2b32c11b8fba9581ec4ba4f1b04215642ef7355e37c0fc0443ef756ea2c6b8eeb755a1c723027663caa265ef785b8ff6a9b35227a52d86633dbdfca43");
    internal static readonly BigInteger G = 2;
    internal static readonly BigInteger K = ParseKprpcHex("b7867f1299da8cc24ab93e08986ebc4d6a478ad0");

    // Subset of KeePassRPC's featuresOffered - just enough to satisfy its version-mismatch tolerance
    // check (ClientSupportsRequiredFeatures) and to identify as a non-Kee client (KPRPC_GENERAL_CLIENTS
    // exists specifically so third-party clients aren't rejected for not being Kee/KeeFox).
    private static readonly string[] OfferedFeatures =
    [
        "KPRPC_FEATURE_VERSION_1_6",
        "KPRPC_GENERAL_CLIENTS",
        "KPRPC_ENTRIES_WITH_NO_URL",
        "KPRPC_FEATURE_WARN_USER_WHEN_FEATURE_MISSING",
    ];

    // KeePassRPC's C# field names are already lowerCamelCase in its own source (e.g. `clientDisplayName`),
    // so CamelCase policy round-trips almost everything correctly - the one exception is `jsonrpc`
    // (all-lowercase, both words joined with no internal capital), overridden explicitly below since
    // the policy alone would produce "jsonRpc".
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly int _port;
    private readonly ILogger<KeePassRpcClient> _logger;
    private ClientWebSocket? _ws;
    private int _nextRpcId = 1;

    public KeePassRpcClient(int port, ILogger<KeePassRpcClient> logger)
    {
        _port = port;
        _logger = logger;
    }

    /// <summary>Opens the WebSocket connection. Throws on any failure (KeePass not running, wrong port, etc.) — callers treat that as "unavailable".</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Origin", OriginHeaderValue);
        await _ws.ConnectAsync(new Uri($"ws://localhost:{_port}/"), ct).ConfigureAwait(false);
    }

    /// <summary>Ephemeral state from <see cref="BeginPairingAsync"/> needed to finish the handshake once the user has typed in the code KeePass shows them.</summary>
    public sealed record PendingPairing(string ClientId, BigInteger PublicA, BigInteger PrivateA, string Salt, string Bstr);

    /// <summary>
    /// Starts a first-time SRP pairing. This makes KeePass pop up its own "authorise a new connection"
    /// dialog showing a randomly-generated code — the human must read that and enter it via
    /// <see cref="CompletePairingAsync"/>. <paramref name="clientId"/> is a stable identity string
    /// (KeePassRPC stores the resulting shared key keyed by this) reused for every future re-auth.
    /// </summary>
    public async Task<PendingPairing> BeginPairingAsync(string clientId, string displayName, string description, CancellationToken ct = default)
    {
        var privateA = RandomBigInteger(32);
        var publicA = BigInteger.ModPow(G, privateA, N);

        await SendAsync(new KprpcMessage
        {
            Protocol = "setup",
            Version = 0,
            Features = OfferedFeatures,
            ClientDisplayName = displayName,
            ClientDisplayDescription = description,
            ClientTypeId = clientId,
            Srp = new KprpcSrpParams { I = clientId, A = ToKprpcHex(publicA), Stage = "identifyToServer", SecurityLevel = 2 },
        }, ct).ConfigureAwait(false);

        var response = await ReceiveAsync(ct).ConfigureAwait(false);
        ThrowIfError(response);

        if (response.Srp is not { Stage: "identifyToClient" } srp || string.IsNullOrEmpty(srp.Salt) || string.IsNullOrEmpty(srp.B))
            throw new KeePassRpcException("Unexpected response from KeePass while starting pairing.");

        return new PendingPairing(clientId, publicA, privateA, srp.Salt, srp.B);
    }

    /// <summary>Finishes pairing using the code the user read from KeePass's own popup. On success, returns the shared key (hex) to persist for future <see cref="AuthenticateAsync"/> calls.</summary>
    public async Task<KeePassPairingResult> CompletePairingAsync(PendingPairing pending, string code, CancellationToken ct = default)
    {
        var aStr = ToKprpcHex(pending.PublicA);
        var x = HashToBigInteger(pending.Salt + code);
        var b = ParseKprpcHex(pending.Bstr);
        var u = HashToBigInteger(aStr + pending.Bstr);

        var gx = BigInteger.ModPow(G, x, N);
        var kgx = ModPositive(K * gx, N);
        var baseVal = ModPositive(b - kgx, N);
        var exponent = pending.PrivateA + u * x;
        var s = BigInteger.ModPow(baseVal, exponent, N);
        var sStr = ToKprpcHex(s);

        var m = HashHex(aStr + pending.Bstr + sStr);

        await SendAsync(new KprpcMessage
        {
            Protocol = "setup",
            Version = 0,
            Srp = new KprpcSrpParams { M = m, Stage = "proofToServer" },
        }, ct).ConfigureAwait(false);

        var response = await ReceiveAsync(ct).ConfigureAwait(false);
        if (response.Error is not null)
            return new KeePassPairingResult(false, null, DescribeError(response.Error));

        if (response.Srp is not { Stage: "proofToClient" } srp || string.IsNullOrEmpty(srp.M2))
            return new KeePassPairingResult(false, null, "Unexpected response from KeePass while finishing pairing.");

        var expectedM2 = HashHex(aStr + m + sStr);
        if (!string.Equals(expectedM2, srp.M2, StringComparison.OrdinalIgnoreCase))
            return new KeePassPairingResult(false, null, "KeePass failed mutual verification — the code was probably wrong.");

        return new KeePassPairingResult(true, HashHex(sStr), null);
    }

    /// <summary>Re-authenticates using a shared key from a previous successful pairing — no user interaction, no popup. Returns false (never throws) on any auth failure.</summary>
    public async Task<bool> AuthenticateAsync(string clientId, string sharedKeyHex, CancellationToken ct = default)
    {
        await SendAsync(new KprpcMessage
        {
            Protocol = "setup",
            Version = 0,
            Features = OfferedFeatures,
            Key = new KprpcKeyParams { Username = clientId, SecurityLevel = 2 },
        }, ct).ConfigureAwait(false);

        var challenge = await ReceiveAsync(ct).ConfigureAwait(false);
        if (challenge.Error is not null || challenge.Key?.Sc is not { Length: > 0 } sc)
        {
            _logger.LogDebug("KeePassRPC rejected the username during re-authentication.");
            return false;
        }

        var ccBytes = RandomNumberGenerator.GetBytes(32);
        var cc = new BigInteger(ccBytes, isUnsigned: true, isBigEndian: true).ToString(CultureInfo.InvariantCulture);
        var cr = HashHex("1" + sharedKeyHex + sc + cc);

        await SendAsync(new KprpcMessage
        {
            Protocol = "setup",
            Version = 0,
            Key = new KprpcKeyParams { Cc = cc, Cr = cr },
        }, ct).ConfigureAwait(false);

        var proof = await ReceiveAsync(ct).ConfigureAwait(false);
        if (proof.Error is not null || proof.Key?.Sr is not { Length: > 0 } sr)
        {
            _logger.LogDebug("KeePassRPC rejected the challenge-response during re-authentication (key may have been revoked).");
            return false;
        }

        var expectedSr = HashHex("0" + sharedKeyHex + sc + cc);
        return string.Equals(expectedSr, sr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Recursively walks every group from the database root and returns every entry found. Must be called after a successful <see cref="AuthenticateAsync"/>.</summary>
    public async Task<IReadOnlyList<KeePassLoginEntry>> BrowseAllEntriesAsync(string sharedKeyHex, CancellationToken ct = default)
    {
        var root = await CallJsonRpcAsync(sharedKeyHex, "GetRoot", [], ct).ConfigureAwait(false);
        var rootUuid = root.GetProperty("UniqueID").GetString()
            ?? throw new KeePassRpcException("KeePass returned a root group with no UniqueID.");

        var entries = new List<KeePassLoginEntry>();
        var groupUuids = new Queue<string>();
        groupUuids.Enqueue(rootUuid);
        var visited = new HashSet<string>();

        while (groupUuids.Count > 0)
        {
            var uuid = groupUuids.Dequeue();
            if (!visited.Add(uuid)) continue;

            var childGroups = await CallJsonRpcAsync(sharedKeyHex, "GetChildGroups", [uuid], ct).ConfigureAwait(false);
            foreach (var g in childGroups.EnumerateArray())
                if (g.TryGetProperty("UniqueID", out var childUuid) && childUuid.GetString() is { Length: > 0 } cu)
                    groupUuids.Enqueue(cu);

            var childEntries = await CallJsonRpcAsync(sharedKeyHex, "GetAllChildEntries", [uuid], ct).ConfigureAwait(false);
            foreach (var e in childEntries.EnumerateArray())
            {
                var entryUuid = e.GetProperty("UniqueID").GetString();
                if (string.IsNullOrEmpty(entryUuid)) continue;
                var title = e.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
                var username = e.TryGetProperty("UsernameValue", out var u) ? u.GetString() : null;
                entries.Add(new KeePassLoginEntry(entryUuid, title, username));
            }
        }

        return entries;
    }

    /// <summary>Finds a single entry by UUID (re-walking the group tree, since KeePassRPC has no get-entry-by-UUID method) and extracts its username/password from its form fields.</summary>
    public async Task<(string? Username, string? Password)?> ResolveEntryAsync(string sharedKeyHex, string uuid, CancellationToken ct = default)
    {
        var root = await CallJsonRpcAsync(sharedKeyHex, "GetRoot", [], ct).ConfigureAwait(false);
        var rootUuid = root.GetProperty("UniqueID").GetString()
            ?? throw new KeePassRpcException("KeePass returned a root group with no UniqueID.");

        var groupUuids = new Queue<string>();
        groupUuids.Enqueue(rootUuid);
        var visited = new HashSet<string>();

        while (groupUuids.Count > 0)
        {
            var groupUuid = groupUuids.Dequeue();
            if (!visited.Add(groupUuid)) continue;

            var childGroups = await CallJsonRpcAsync(sharedKeyHex, "GetChildGroups", [groupUuid], ct).ConfigureAwait(false);
            foreach (var g in childGroups.EnumerateArray())
                if (g.TryGetProperty("UniqueID", out var childUuid) && childUuid.GetString() is { Length: > 0 } cu)
                    groupUuids.Enqueue(cu);

            var childEntries = await CallJsonRpcAsync(sharedKeyHex, "GetAllChildEntries", [groupUuid], ct).ConfigureAwait(false);
            foreach (var e in childEntries.EnumerateArray())
            {
                if (e.GetProperty("UniqueID").GetString() != uuid) continue;

                var username = e.TryGetProperty("UsernameValue", out var u) ? u.GetString() : null;
                string? password = null;
                if (e.TryGetProperty("FormFieldList", out var fields) && fields.ValueKind == JsonValueKind.Array)
                {
                    foreach (var field in fields.EnumerateArray())
                    {
                        // FormFieldType.FFTpassword = 3 (FFTradio=0, FFTusername=1, FFTtext=2, FFTpassword=3, ...)
                        if (field.TryGetProperty("Type", out var type) && type.ValueKind == JsonValueKind.Number && type.GetInt32() == 3
                            && field.TryGetProperty("Value", out var val))
                        {
                            password = val.GetString();
                            break;
                        }
                    }
                }
                return (username, password);
            }
        }

        return null;
    }

    private async Task<JsonElement> CallJsonRpcAsync(string sharedKeyHex, string method, object[] paramsArray, CancellationToken ct)
    {
        var id = _nextRpcId++;
        var request = JsonSerializer.Serialize(new { id, method, @params = paramsArray });
        var container = Encrypt(request, sharedKeyHex);

        await SendAsync(new KprpcMessage { Protocol = "jsonrpc", Version = 0, JsonRpc = container }, ct).ConfigureAwait(false);
        var response = await ReceiveAsync(ct).ConfigureAwait(false);
        ThrowIfError(response);

        if (response.JsonRpc is null)
            throw new KeePassRpcException("KeePass sent no JSON-RPC response.");

        var plaintext = Decrypt(response.JsonRpc, sharedKeyHex)
            ?? throw new KeePassRpcException("Could not decrypt KeePass's response (MAC mismatch — shared key may be stale).");

        using var doc = JsonDocument.Parse(plaintext);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            throw new KeePassRpcException($"KeePassRPC call '{method}' failed: {err}");

        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    // --- SRP big-integer helpers -------------------------------------------------------------
    // KeePassRPC's own BigInteger.ToString(16) produces uppercase hex with no leading-zero
    // padding (built via repeated division, not a fixed-width format) and its byte[] constructor
    // treats the array as big-endian unsigned. System.Numerics.BigInteger's hex formatting is
    // two's-complement (may prepend one '0' to keep a value positive) and its default byte[]
    // constructor is little-endian two's-complement - both need translating to match exactly, or
    // the hash inputs (and therefore every derived key) silently diverge from KeePass's own.

    internal static BigInteger ParseKprpcHex(string hex) =>
        BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    internal static string ToKprpcHex(BigInteger value)
    {
        if (value.IsZero) return "0";
        var hex = value.ToString("X", CultureInfo.InvariantCulture).TrimStart('0');
        return hex.Length > 0 ? hex : "0";
    }

    internal static BigInteger HashToBigInteger(string data) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(data)), isUnsigned: true, isBigEndian: true);

    internal static string HashHex(string data) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    internal static BigInteger RandomBigInteger(int byteCount) =>
        new(RandomNumberGenerator.GetBytes(byteCount), isUnsigned: true, isBigEndian: true);

    internal static BigInteger ModPositive(BigInteger value, BigInteger modulus)
    {
        var r = value % modulus;
        return r < 0 ? r + modulus : r;
    }

    // --- Encrypted JSON-RPC envelope ---------------------------------------------------------
    // Matches KeePassRPCClient.cs's Encrypt/Decrypt exactly: RijndaelManaged left at its library
    // defaults (256-bit block/key, CBC, PKCS7 - deliberately NOT System.Security.Cryptography.Aes,
    // which is hardcoded to a 128-bit block and would silently produce incompatible ciphertext),
    // keyed by the raw shared-key bytes, with a legacy (non-HMAC-construction) integrity tag:
    // SHA1(SHA1(key) || ciphertext || IV). Both algorithm choices are KeePassRPC's, not ours.
#pragma warning disable SYSLIB0022 // RijndaelManaged is obsolete - required for wire compatibility with KeePassRPC's real protocol (256-bit block, not AES's fixed 128-bit)
#pragma warning disable CA5350 // SHA1 is weak - but this is KeePassRPC's own (non-HMAC) integrity-tag construction, not a security choice we get to make; matching it exactly is required for interop
    internal static KprpcJsonRpcContainer Encrypt(string plaintext, string sharedKeyHex)
    {
        var keyBytes = Convert.FromHexString(sharedKeyHex);
        using var rijndael = new RijndaelManaged();
        rijndael.GenerateIV();
        rijndael.Key = keyBytes;

        byte[] encrypted;
        using (var encryptor = rijndael.CreateEncryptor())
        using (var ms = new MemoryStream())
        {
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            {
                var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
                cs.Write(plaintextBytes, 0, plaintextBytes.Length);
                cs.FlushFinalBlock();
            }
            encrypted = ms.ToArray();
        }

        var hmacKey = SHA1.HashData(rijndael.Key);
        var hmacSource = new byte[hmacKey.Length + encrypted.Length + rijndael.IV.Length];
        Buffer.BlockCopy(hmacKey, 0, hmacSource, 0, hmacKey.Length);
        Buffer.BlockCopy(encrypted, 0, hmacSource, hmacKey.Length, encrypted.Length);
        Buffer.BlockCopy(rijndael.IV, 0, hmacSource, hmacKey.Length + encrypted.Length, rijndael.IV.Length);
        var hmac = SHA1.HashData(hmacSource);

        return new KprpcJsonRpcContainer
        {
            Iv = Convert.ToBase64String(rijndael.IV),
            Message = Convert.ToBase64String(encrypted),
            Hmac = Convert.ToBase64String(hmac),
        };
    }

    internal static string? Decrypt(KprpcJsonRpcContainer container, string sharedKeyHex)
    {
        if (string.IsNullOrEmpty(container.Message) || string.IsNullOrEmpty(container.Iv) || string.IsNullOrEmpty(container.Hmac))
            return null;

        byte[] rawKeyBytes, messageBytes, ivBytes;
        try
        {
            rawKeyBytes = Convert.FromHexString(sharedKeyHex);
            messageBytes = Convert.FromBase64String(container.Message);
            ivBytes = Convert.FromBase64String(container.Iv);
        }
        catch (FormatException)
        {
            return null;
        }

        var keyHash = SHA1.HashData(rawKeyBytes);
        var hmacSource = new byte[keyHash.Length + messageBytes.Length + ivBytes.Length];
        Buffer.BlockCopy(keyHash, 0, hmacSource, 0, keyHash.Length);
        Buffer.BlockCopy(messageBytes, 0, hmacSource, keyHash.Length, messageBytes.Length);
        Buffer.BlockCopy(ivBytes, 0, hmacSource, keyHash.Length + messageBytes.Length, ivBytes.Length);
        var expectedHmac = SHA1.HashData(hmacSource);
        if (Convert.ToBase64String(expectedHmac) != container.Hmac) return null;

        using var rijndael = new RijndaelManaged();
        using var decryptor = rijndael.CreateDecryptor(rawKeyBytes, ivBytes);
        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Write))
        {
            cs.Write(messageBytes, 0, messageBytes.Length);
            cs.FlushFinalBlock();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
#pragma warning restore CA5350
#pragma warning restore SYSLIB0022

    // --- Transport ----------------------------------------------------------------------------

    private async Task SendAsync(KprpcMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task<KprpcMessage> ReceiveAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await _ws!.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new KeePassRpcException("KeePass closed the connection unexpectedly.");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(ms.ToArray());
        return JsonSerializer.Deserialize<KprpcMessage>(json, JsonOptions)
            ?? throw new KeePassRpcException("Empty response from KeePass.");
    }

    private static void ThrowIfError(KprpcMessage message)
    {
        if (message.Error is not null)
            throw new KeePassRpcException(DescribeError(message.Error));
    }

    private static string DescribeError(KprpcError error) =>
        error.MessageParams is { Length: > 0 }
            ? $"KeePass rejected the request (code {error.Code}): {string.Join(", ", error.MessageParams)}"
            : $"KeePass rejected the request (code {error.Code}).";

    public ValueTask DisposeAsync()
    {
        if (_ws is null) return ValueTask.CompletedTask;
        // Deliberately Abort() rather than CloseAsync(): this is a short-lived
        // connect-authenticate-call-disconnect client (a fresh WebSocket per credential
        // resolve/browse, not a long-lived session), so nothing depends on the server observing a
        // clean close handshake - and CloseAsync's close handshake reliably crashed the process
        // outright against a real WebSocket server in testing (reproduced against a real
        // HttpListener-backed server, not a mock - not just a timeout/exception, an actual process
        // crash), so this avoids that path entirely rather than trying to catch it.
        _ws.Abort();
        _ws.Dispose();
        return ValueTask.CompletedTask;
    }

    // --- Wire DTOs — field names verified against kee-org/keepassrpc's KPRPCMessage/SRPParams/KeyParams/JSONRPCContainer/Error ---

    private sealed class KprpcMessage
    {
        public string? Protocol { get; set; }
        [JsonPropertyName("jsonrpc")] public KprpcJsonRpcContainer? JsonRpc { get; set; }
        public KprpcSrpParams? Srp { get; set; }
        public KprpcKeyParams? Key { get; set; }
        public int Version { get; set; }
        public string? ClientDisplayName { get; set; }
        public string? ClientDisplayDescription { get; set; }
        public string? ClientTypeId { get; set; }
        public KprpcError? Error { get; set; }
        public string[]? Features { get; set; }
    }

    private sealed class KprpcSrpParams
    {
        [JsonPropertyName("I")] public string? I { get; set; }
        [JsonPropertyName("A")] public string? A { get; set; }
        [JsonPropertyName("B")] public string? B { get; set; }
        [JsonPropertyName("M")] public string? M { get; set; }
        [JsonPropertyName("M2")] public string? M2 { get; set; }
        [JsonPropertyName("s")] public string? Salt { get; set; }
        public string? Stage { get; set; }
        public int SecurityLevel { get; set; }
    }

    private sealed class KprpcKeyParams
    {
        public string? Username { get; set; }
        public int SecurityLevel { get; set; }
        public string? Cc { get; set; }
        public string? Cr { get; set; }
        public string? Sc { get; set; }
        public string? Sr { get; set; }
    }

    internal sealed class KprpcJsonRpcContainer
    {
        public string? Message { get; set; }
        public string? Iv { get; set; }
        public string? Hmac { get; set; }
    }

    private sealed class KprpcError
    {
        public int Code { get; set; }
        public string[]? MessageParams { get; set; }
    }
}
