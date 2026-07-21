using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine.Credentials;

namespace PolyTerm.App.Views;

/// <summary>
/// Lets the user turn on the KeePass or Bitwarden credential provider (issue #73) — previously there
/// was no config path, GUI or otherwise, to enable either. Takes effect on the next launch: the DI
/// container (and therefore which <c>ICredentialProvider</c>s are registered) is built once at
/// startup in <see cref="AppCompositionRoot.BuildServiceProvider"/>, before this window can exist.
///
/// Also hosts the Bitwarden in-app login/unlock flow and a persistent, auto-refreshing status
/// indicator for both providers (issue #78) — previously the only signal was a one-shot "Test
/// Connection" button, and Bitwarden had no in-app way to get unlocked at all.
/// </summary>
public sealed partial class VaultSettingsWindow : Window, IDisposable
{
    private static readonly ISolidColorBrush s_okBrush = new SolidColorBrush(Color.FromRgb(0x4c, 0xaf, 0x50));
    private static readonly ISolidColorBrush s_failBrush = new SolidColorBrush(Color.FromRgb(0xe0, 0x57, 0x57));
    private static readonly TimeSpan s_statusPollInterval = TimeSpan.FromSeconds(5);

    private Timer? _statusTimer;
    private int _statusCheckInFlight; // 0/1 guard so overlapping ticks are skipped, not queued — same convention as ComPortWatcherService.

    public VaultSettingsWindow()
    {
        InitializeComponent();

        ProviderCombo.SelectedIndex = App.Settings.VaultProvider switch
        {
            "KeePass" => 1,
            "Bitwarden" => 2,
            _ => 0,
        };
        KeePassPortBox.Text = App.Settings.KeePassRpcPort.ToString(CultureInfo.InvariantCulture);
        BitwardenUrlBox.Text = App.Settings.BitwardenBaseUrl;
        UpdatePanelVisibility();

        _statusTimer = new Timer(_ => PollStatus(), null, s_statusPollInterval, s_statusPollInterval);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _statusTimer?.Dispose();
        _statusTimer = null;
    }

    private void ProviderCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdatePanelVisibility();

    private void UpdatePanelVisibility()
    {
        KeePassPanel.IsVisible = ProviderCombo.SelectedIndex == 1;
        BitwardenPanel.IsVisible = ProviderCombo.SelectedIndex == 2;
        TestConnectionPanel.IsVisible = ProviderCombo.SelectedIndex != 0;
        // BitwardenApiClient is only registered when the *saved* provider is already "Bitwarden"
        // (see AppCompositionRoot) — a provider switched to just now in this still-open, unsaved combo
        // isn't wired into the running instance's DI container yet. Save + restart with Bitwarden
        // selected, then reopen, to log in.
        var bitwardenActive = ProviderCombo.SelectedIndex == 2 && App.Settings.VaultProvider == "Bitwarden";
        BitwardenLoginButton.IsVisible = bitwardenActive;
        BitwardenForgetDeviceButton.IsVisible = bitwardenActive;
        TestConnectionResult.Text = "";
    }

    /// <summary>
    /// Ticks every <see cref="s_statusPollInterval"/> while this window is open, silently refreshing
    /// <see cref="TestConnectionResult"/> for whichever provider is currently selected. The timer
    /// callback itself runs on a thread-pool thread, so it only ever posts work onto the UI thread —
    /// <see cref="CheckKeePassAsync"/>/<see cref="CheckBitwardenAsync"/> read Avalonia TextBoxes and
    /// must not be called off-thread.
    /// </summary>
    private void PollStatus()
    {
        if (Interlocked.CompareExchange(ref _statusCheckInFlight, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (ProviderCombo.SelectedIndex == 0) return; // "None" — no provider to check.

                var (ok, message) = ProviderCombo.SelectedIndex == 1
                    ? await CheckKeePassAsync().ConfigureAwait(true)
                    : await CheckBitwardenAsync().ConfigureAwait(true);

                TestConnectionResult.Foreground = ok ? s_okBrush : s_failBrush;
                TestConnectionResult.Text = message;
            }
            finally
            {
                Interlocked.Exchange(ref _statusCheckInFlight, 0);
            }
        });
    }

    /// <summary>
    /// Opens <see cref="BitwardenLoginDialog"/> to log in or unlock the Bitwarden vault in-app
    /// (issue #78), then immediately refreshes the status line to reflect the result.
    /// </summary>
    private async void BitwardenLogin_Click(object? sender, RoutedEventArgs e)
    {
        var controller = App.Services?.GetService<BitwardenApiClient>();
        if (controller is null)
        {
            TestConnectionResult.Foreground = s_failBrush;
            TestConnectionResult.Text = "Bitwarden isn't active in this running instance — save and restart first.";
            return;
        }

        BitwardenLoginButton.IsEnabled = false;
        try
        {
            var status = await controller.CheckStatusAsync().ConfigureAwait(true);
            // Reachable more often now that auto-unlock exists (login persistence) - previously this
            // was effectively dead code, since nothing unlocked the vault outside an explicit dialog
            // flow. BitwardenLoginDialog's heading text assumes Unauthenticated/Locked, not Unlocked.
            if (status == BitwardenAuthStatus.Unlocked)
            {
                TestConnectionResult.Foreground = s_okBrush;
                TestConnectionResult.Text = "Vault is already unlocked.";
                return;
            }

            var dialog = new BitwardenLoginDialog(controller, status);
            var loggedIn = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
            if (loggedIn)
            {
                TestConnectionResult.Foreground = s_okBrush;
                TestConnectionResult.Text = "Logged in — the vault is unlocked.";
            }
        }
        finally
        {
            BitwardenLoginButton.IsEnabled = true;
        }
    }

    /// <summary>Removes a "Remember this device" OS-keychain entry, if one exists, for the current account (login persistence).</summary>
    private async void BitwardenForgetDevice_Click(object? sender, RoutedEventArgs e)
    {
        var controller = App.Services?.GetService<BitwardenApiClient>();
        if (controller is null || App.Settings.BitwardenLastEmail is not { Length: > 0 } email)
        {
            TestConnectionResult.Foreground = s_failBrush;
            TestConnectionResult.Text = "Nothing to forget — no remembered account for this instance.";
            return;
        }

        BitwardenForgetDeviceButton.IsEnabled = false;
        try
        {
            await controller.ForgetDeviceAsync(email).ConfigureAwait(true);
            TestConnectionResult.Foreground = s_okBrush;
            TestConnectionResult.Text = "This device has been forgotten — the master password will be required next launch.";
        }
        finally
        {
            BitwardenForgetDeviceButton.IsEnabled = true;
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings.VaultProvider = ProviderCombo.SelectedIndex switch
        {
            1 => "KeePass",
            2 => "Bitwarden",
            _ => "None",
        };
        if (int.TryParse(KeePassPortBox.Text, out var port) && port > 0)
            App.Settings.KeePassRpcPort = port;
        if (!string.IsNullOrWhiteSpace(BitwardenUrlBox.Text))
            App.Settings.BitwardenBaseUrl = BitwardenUrlBox.Text.Trim();

        App.Settings.Save();
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Checks reachability (and, for Bitwarden, unlocked-state) directly against whatever's currently
    /// typed in the field — not necessarily saved yet — so a user can verify before committing.
    /// Neither <c>KeePassProvider</c> nor <c>BitwardenProvider</c> (both <c>internal</c> to
    /// <c>PolyTerm.Engine</c>) is reachable from here, so this deliberately re-implements each
    /// provider's own <c>IsAvailableAsync</c> check byte-for-byte rather than exposing them — this is
    /// diagnostic-only, not a substitute for a real in-app unlock flow (which neither provider has at
    /// all; see the follow-up issue filed alongside this).
    /// </summary>
    private async void TestConnection_Click(object? sender, RoutedEventArgs e)
    {
        TestConnectionButton.IsEnabled = false;
        TestConnectionResult.Foreground = null;
        TestConnectionResult.Text = "Checking…";

        try
        {
            var (ok, message) = ProviderCombo.SelectedIndex == 1
                ? await CheckKeePassAsync().ConfigureAwait(true)
                : await CheckBitwardenAsync().ConfigureAwait(true);

            TestConnectionResult.Foreground = ok ? s_okBrush : s_failBrush;
            TestConnectionResult.Text = message;
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    /// <summary>Mirrors <c>KeePassProvider.IsAvailableAsync</c> exactly: GET <c>/ping</c>, success status = reachable.</summary>
    private async Task<(bool Ok, string Message)> CheckKeePassAsync()
    {
        if (!int.TryParse(KeePassPortBox.Text, out var port) || port <= 0)
            return (false, "Enter a valid port number first.");

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/"), Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync("ping").ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? (true, "KeePassRPC is reachable.")
                : (false, $"KeePassRPC responded with {(int)resp.StatusCode}. Is KeePass running with the RPC plugin enabled?");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, "Not reachable — is KeePass running with the RPC plugin enabled on this port?");
        }
    }

    /// <summary>
    /// If Bitwarden is already the active saved provider, reports the real, live session status from
    /// <see cref="BitwardenApiClient"/> (issue #78 — logging in/unlocking now happens entirely through
    /// this class, no external <c>bw serve</c> daemon anymore). Otherwise falls back to a raw
    /// reachability check against whatever URL is currently typed — a self-hosted Vaultwarden's
    /// <c>prelogin</c> endpoint always answers 200 without confirming or denying an account exists
    /// (avoids email enumeration), so it's a safe, harmless probe for "is this even a vault server".
    /// </summary>
    private async Task<(bool Ok, string Message)> CheckBitwardenAsync()
    {
        var client = App.Services?.GetService<BitwardenApiClient>();
        if (client is not null)
        {
            return client.CheckStatus() switch
            {
                BitwardenAuthStatus.Unlocked => (true, "Logged in — the vault is unlocked."),
                BitwardenAuthStatus.Locked => (false, "Logged in earlier this session, but locked — use Log In / Unlock to re-enter your master password."),
                _ => (false, "Not logged in yet — use Log In / Unlock."),
            };
        }

        var baseUrl = BitwardenUrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(baseUrl))
            return (false, "Enter a base URL first.");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/identity/accounts/prelogin",
                new { email = "polyterm-reachability-check@example.invalid" }).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? (true, "Server is reachable. Save and restart, then use Log In / Unlock to sign in.")
                : (false, $"Server responded with {(int)resp.StatusCode} — check the URL.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, "Not reachable — check the URL.");
        }
    }
}
