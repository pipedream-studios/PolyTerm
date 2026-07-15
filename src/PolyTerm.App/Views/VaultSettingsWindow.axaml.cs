using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace PolyTerm.App.Views;

/// <summary>
/// Lets the user turn on the KeePass or Bitwarden credential provider (issue #73) — previously there
/// was no config path, GUI or otherwise, to enable either. Takes effect on the next launch: the DI
/// container (and therefore which <c>ICredentialProvider</c>s are registered) is built once at
/// startup in <see cref="AppCompositionRoot.BuildServiceProvider"/>, before this window can exist.
/// </summary>
public sealed partial class VaultSettingsWindow : Window
{
    private static readonly ISolidColorBrush s_okBrush = new SolidColorBrush(Color.FromRgb(0x4c, 0xaf, 0x50));
    private static readonly ISolidColorBrush s_failBrush = new SolidColorBrush(Color.FromRgb(0xe0, 0x57, 0x57));

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
    }

    private void ProviderCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdatePanelVisibility();

    private void UpdatePanelVisibility()
    {
        KeePassPanel.IsVisible = ProviderCombo.SelectedIndex == 1;
        BitwardenPanel.IsVisible = ProviderCombo.SelectedIndex == 2;
        TestConnectionPanel.IsVisible = ProviderCombo.SelectedIndex != 0;
        TestConnectionResult.Text = "";
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

    /// <summary>Mirrors <c>BitwardenProvider.IsAvailableAsync</c> exactly: GET <c>/status</c>, unlocked only when <c>data.template.status == "unlocked"</c>.</summary>
    private async Task<(bool Ok, string Message)> CheckBitwardenAsync()
    {
        var baseUrl = BitwardenUrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(baseUrl))
            return (false, "Enter a base URL first.");

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync("status").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, $"bw serve responded with {(int)resp.StatusCode}. Is it running at this URL?");

            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var status = doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("template", out var tmpl)
                && tmpl.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;

            return status switch
            {
                "unlocked" => (true, "bw serve is reachable and the vault is unlocked."),
                "locked" => (false, "bw serve is reachable but the vault is locked — run 'bw unlock' and re-serve with that session."),
                "unauthenticated" => (false, "bw serve is reachable but not logged in — run 'bw login' first."),
                _ => (false, $"bw serve is reachable but reported an unexpected status ({status ?? "none"})."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, "Not reachable — is 'bw serve' running at this URL?");
        }
    }
}
