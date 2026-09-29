using System.Net;
using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.App.Views;
using PolyTerm.Engine.Credentials;
using PolyTerm.Engine.Storage;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Drives <see cref="KeePassPairingDialog"/> (issue #120's in-app SRP pairing flow) end-to-end against a
/// real fake WebSocket server — not just constructor/property assertions — since opening the dialog is
/// what actually triggers the handshake (<c>OnOpened</c> → <see cref="KeePassRpcClient.BeginPairingAsync"/>).
/// The fake server here is a trimmed-down sibling of <c>KeePassRpcClientTests</c>' one (SRP pairing
/// only, no JSON-RPC — this dialog never calls that), reusing the same protocol-constant crypto
/// primitives from <see cref="KeePassRpcClient"/> via <c>InternalsVisibleTo</c>.
/// </summary>
public sealed class KeePassPairingDialogTests
{
    private const string PairingCode = "TESTCODE1234";

    [AvaloniaFact]
    public async Task Opening_RunsTheHandshake_AndConfirmingWithTheRightCode_ClosesTrue_AndPersistsThePairing()
    {
        using var server = new FakeKeePassPairingServer(PairingCode);
        var pairingStore = new KeePassPairingStore(TempDbPath(), NullLogger<KeePassPairingStore>.Instance);

        var owner = new Window();
        owner.Show();
        var dialog = new KeePassPairingDialog(server.Port, pairingStore, NullLogger<KeePassRpcClient>.Instance);
        var resultTask = dialog.ShowDialog<bool>(owner);

        // CodeBox itself has no explicit IsVisible in the XAML (only its parent CodePanel does), so it
        // reads as visible from construction onward - the real signal that the handshake reached the
        // "enter the code" stage is CodePanel becoming visible, not CodeBox.
        var codePanel = dialog.FindControl<StackPanel>("CodePanel")!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!codePanel.IsVisible && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        codePanel.IsVisible.Should().BeTrue($"status was: {dialog.FindControl<TextBlock>("StatusText")!.Text} / error: {dialog.FindControl<TextBlock>("ErrorText")!.Text}");

        dialog.FindControl<TextBox>("CodeBox")!.Text = PairingCode;
        dialog.FindControl<Button>("ConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().BeTrue();

        var saved = await pairingStore.FindAsync();
        saved.Should().NotBeNull();
        saved!.ClientId.Should().Be("PolyTerm");
        saved.SharedKeyHex.Should().Be(server.PairedSharedKeyHex);
    }

    [AvaloniaFact]
    public async Task Opening_RunsTheHandshake_AndConfirmingWithTheWrongCode_ShowsAnError_AndDoesNotClose()
    {
        using var server = new FakeKeePassPairingServer(PairingCode);
        var pairingStore = new KeePassPairingStore(TempDbPath(), NullLogger<KeePassPairingStore>.Instance);

        var owner = new Window();
        owner.Show();
        var dialog = new KeePassPairingDialog(server.Port, pairingStore, NullLogger<KeePassRpcClient>.Instance);
        var resultTask = dialog.ShowDialog<bool>(owner);

        var codePanel = dialog.FindControl<StackPanel>("CodePanel")!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!codePanel.IsVisible && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        codePanel.IsVisible.Should().BeTrue("the handshake should have reached the point of asking for the code KeePass displayed");

        dialog.FindControl<TextBox>("CodeBox")!.Text = "the wrong code entirely";
        dialog.FindControl<Button>("ConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var errorText = dialog.FindControl<TextBlock>("ErrorText")!;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!errorText.IsVisible && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        errorText.IsVisible.Should().BeTrue();
        resultTask.IsCompleted.Should().BeFalse("a failed attempt should let the user retry, not close the dialog");
        (await pairingStore.FindAsync()).Should().BeNull("a failed proof must not persist anything");

        dialog.Close(false); // tidy up the still-open dialog
    }

    [AvaloniaFact]
    public void ClickingCancel_ClosesFalse_WithoutPairing()
    {
        var owner = new Window();
        owner.Show();
        // Port 0 with nothing listening - Cancel must work even if the handshake never got anywhere.
        var pairingStore = new KeePassPairingStore(TempDbPath(), NullLogger<KeePassPairingStore>.Instance);
        var dialog = new KeePassPairingDialog(GetFreeTcpPort(), pairingStore, NullLogger<KeePassRpcClient>.Instance);
        var resultTask = dialog.ShowDialog<bool>(owner);

        dialog.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        resultTask.IsCompletedSuccessfully.Should().BeTrue();
        resultTask.Result.Should().BeFalse();
    }

    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"polyterm-test-{Guid.NewGuid():N}.db");

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>Trimmed-down sibling of <c>KeePassRpcClientTests</c>' fake server — SRP pairing only, no JSON-RPC.</summary>
file sealed class FakeKeePassPairingServer : IDisposable
{
    private readonly string _pairingCode;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private static readonly string[] s_keysDoNotMatch = ["Keys do not match"];

    public int Port { get; }
    public string? PairedSharedKeyHex { get; private set; }

    public FakeKeePassPairingServer(string pairingCode)
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

            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
            var origin = ctx.Request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin) || !origin.StartsWith("polyterm://", StringComparison.Ordinal))
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
        string? astr = null;
        BigInteger a = default, v = default, bPriv = default, bPub = default;
        string? salt = null, bstr = null, pendingClientId = null;

        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var messageJson = await ReceiveTextAsync(ws, _cts.Token).ConfigureAwait(false);
                if (messageJson is null) return;

                using var doc = JsonDocument.Parse(messageJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("srp", out var srp) || srp.ValueKind != JsonValueKind.Object
                    || !srp.TryGetProperty("stage", out var stageEl)) continue;

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
                    PairedSharedKeyHex = KeePassRpcClient.HashHex(sstr);
                    _ = pendingClientId;

                    await SendTextAsync(ws, JsonSerializer.Serialize(new
                    {
                        protocol = "setup", version = 0,
                        srp = new { stage = "proofToClient", M2 = m2 },
                    }), _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Expected: KeePassRpcClient.DisposeAsync() Aborts rather than closing gracefully.
        }
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
