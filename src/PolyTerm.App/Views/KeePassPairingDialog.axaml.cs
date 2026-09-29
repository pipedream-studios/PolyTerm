using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine.Credentials;
using PolyTerm.Engine.Storage;

namespace PolyTerm.App.Views;

/// <summary>
/// Drives a real KeePassRPC SRP pairing handshake (issue #120's MVP scope) in-app. Unlike
/// <see cref="BitwardenLoginDialog"/>, this never collects a password from the user directly — opening
/// the dialog immediately makes KeePass pop up its own "authorise a new connection" window showing a
/// randomly-generated code, and the human's only job here is to read that and type it in. Closes with
/// <c>true</c> only once pairing has actually succeeded and been persisted to
/// <see cref="KeePassPairingStore"/>.
/// </summary>
public sealed partial class KeePassPairingDialog : Window, IAsyncDisposable
{
    // KeePassRPC stores its persisted shared key keyed by this identity - reused for every future
    // re-authentication, so it must stay stable across app runs rather than being freshly generated
    // per pairing attempt.
    private const string ClientId = "PolyTerm";

    private int _port;
    private KeePassPairingStore _pairingStore = null!; // set by the real (non-XAML-loader) constructor below
    private ILogger<KeePassRpcClient> _clientLogger = null!;
    private KeePassRpcClient? _client;
    private KeePassRpcClient.PendingPairing? _pending;

    public KeePassPairingDialog() => InitializeComponent();

    public KeePassPairingDialog(int port, KeePassPairingStore pairingStore, ILogger<KeePassRpcClient> clientLogger) : this()
    {
        _port = port;
        _pairingStore = pairingStore;
        _clientLogger = clientLogger;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await StartPairingAsync().ConfigureAwait(true);
    }

    private async Task StartPairingAsync()
    {
        StatusText.Text = "Connecting to KeePass…";
        try
        {
            _client = new KeePassRpcClient(_port, _clientLogger);
            await _client.ConnectAsync().ConfigureAwait(true);

            StatusText.Text = "Requesting pairing — check KeePass for a popup asking to authorise this connection…";
            _pending = await _client.BeginPairingAsync(ClientId, "PolyTerm", "PolyTerm remote connections manager").ConfigureAwait(true);

            StatusText.Text = "KeePass should now be showing a popup with a code. Enter that code below and click Confirm.";
            CodePanel.IsVisible = true;
            CodeBox.Focus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not start pairing.";
            ShowError($"{ex.Message} Make sure KeePass is running with the KeePassRPC plugin on port {_port}, and that " +
                      $"\"{KeePassRpcClient.OriginHeaderValue}\" has been added to Allowed Origins under Tools → KeePassRPC Options.");
            ConfirmButton.IsEnabled = false;
        }
    }

    private async void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        if (_client is null || _pending is null) return;

        var code = CodeBox.Text?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            ShowError("Enter the code shown in KeePass.");
            return;
        }

        ErrorText.IsVisible = false;
        SetBusy(true);
        try
        {
            var result = await _client.CompletePairingAsync(_pending, code).ConfigureAwait(true);
            if (!result.Success)
            {
                ShowError(result.ErrorMessage ?? "Pairing failed.");
                return;
            }

            await _pairingStore.SaveAsync(new KeePassPairingRecord(_pending.ClientId, result.SharedKeyHex!, DateTimeOffset.UtcNow)).ConfigureAwait(true);
            Close(true);
        }
        catch (Exception ex)
        {
            ShowError($"Pairing failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        ConfirmButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await DisposeAsync().ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        await _client.DisposeAsync().ConfigureAwait(true);
        _client = null;
    }
}
