using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AwesomeAssertions;
using PolyTerm.App.Views;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="VaultSettingsWindow"/> (issue #73's GUI on-switch for the KeePass/Bitwarden
/// providers). Deliberately never exercises <c>Save_Click</c> — it calls the real
/// <see cref="App.Settings"/>.Save(), which writes the real per-user settings.json on disk with no
/// test-isolation seam (matching this file's own <see cref="AppSettingsTestCollection"/> convention:
/// every existing test that touches <see cref="App.Settings"/> mutates the in-memory singleton only,
/// never calls <c>.Save()</c>). Covers the read side (populating from current settings) and the pure
/// UI toggle (provider selection showing/hiding the matching options panel) instead.
/// </summary>
[Collection("AppSettings")]
public sealed class VaultSettingsWindowTests
{
    private readonly string _originalProvider;
    private readonly int _originalKeePassPort;
    private readonly string _originalBitwardenUrl;

    public VaultSettingsWindowTests()
    {
        _originalProvider = App.Settings.VaultProvider;
        _originalKeePassPort = App.Settings.KeePassRpcPort;
        _originalBitwardenUrl = App.Settings.BitwardenBaseUrl;
    }

    private void RestoreSettings()
    {
        App.Settings.VaultProvider = _originalProvider;
        App.Settings.KeePassRpcPort = _originalKeePassPort;
        App.Settings.BitwardenBaseUrl = _originalBitwardenUrl;
    }

    [AvaloniaFact]
    public void Constructor_PopulatesFieldsFromCurrentSettings_WhenKeePassIsSelected()
    {
        App.Settings.VaultProvider = "KeePass";
        App.Settings.KeePassRpcPort = 12345;
        try
        {
            var window = new VaultSettingsWindow();

            var combo = window.FindControl<ComboBox>("ProviderCombo");
            combo!.SelectedIndex.Should().Be(1, "KeePass is index 1 in the ComboBox");

            var portBox = window.FindControl<TextBox>("KeePassPortBox");
            portBox!.Text.Should().Be("12345");

            var keePassPanel = window.FindControl<StackPanel>("KeePassPanel");
            keePassPanel!.IsVisible.Should().BeTrue("the KeePass options panel must be visible when KeePass is the current provider");

            var bitwardenPanel = window.FindControl<StackPanel>("BitwardenPanel");
            bitwardenPanel!.IsVisible.Should().BeFalse();

            window.FindControl<StackPanel>("TestConnectionPanel")!.IsVisible.Should().BeTrue("Test Connection is offered for any real provider, not just None");
        }
        finally { RestoreSettings(); }
    }

    [AvaloniaFact]
    public void Constructor_PopulatesFieldsFromCurrentSettings_WhenBitwardenIsSelected()
    {
        App.Settings.VaultProvider = "Bitwarden";
        App.Settings.BitwardenBaseUrl = "http://localhost:9999";
        try
        {
            var window = new VaultSettingsWindow();

            window.FindControl<ComboBox>("ProviderCombo")!.SelectedIndex.Should().Be(2, "Bitwarden is index 2 in the ComboBox");
            window.FindControl<TextBox>("BitwardenUrlBox")!.Text.Should().Be("http://localhost:9999");
            window.FindControl<StackPanel>("BitwardenPanel")!.IsVisible.Should().BeTrue();
            window.FindControl<StackPanel>("KeePassPanel")!.IsVisible.Should().BeFalse();
        }
        finally { RestoreSettings(); }
    }

    [AvaloniaFact]
    public void Constructor_ShowsNeitherPanel_WhenNoProviderIsSelected()
    {
        App.Settings.VaultProvider = "None";
        try
        {
            var window = new VaultSettingsWindow();

            window.FindControl<ComboBox>("ProviderCombo")!.SelectedIndex.Should().Be(0);
            window.FindControl<StackPanel>("KeePassPanel")!.IsVisible.Should().BeFalse();
            window.FindControl<StackPanel>("BitwardenPanel")!.IsVisible.Should().BeFalse();
            window.FindControl<StackPanel>("TestConnectionPanel")!.IsVisible.Should().BeFalse("nothing to test when the OS keychain — which has no reachability concept — is the only thing selected");
        }
        finally { RestoreSettings(); }
    }

    [AvaloniaFact]
    public void ChangingProviderSelection_TogglesTheMatchingOptionsPanel()
    {
        try
        {
            var window = new VaultSettingsWindow();
            var combo = window.FindControl<ComboBox>("ProviderCombo")!;

            combo.SelectedIndex = 1; // KeePass
            window.FindControl<StackPanel>("KeePassPanel")!.IsVisible.Should().BeTrue();
            window.FindControl<StackPanel>("BitwardenPanel")!.IsVisible.Should().BeFalse();
            window.FindControl<StackPanel>("TestConnectionPanel")!.IsVisible.Should().BeTrue();

            combo.SelectedIndex = 2; // Bitwarden
            window.FindControl<StackPanel>("KeePassPanel")!.IsVisible.Should().BeFalse();
            window.FindControl<StackPanel>("BitwardenPanel")!.IsVisible.Should().BeTrue();
            window.FindControl<StackPanel>("TestConnectionPanel")!.IsVisible.Should().BeTrue();

            combo.SelectedIndex = 0; // None
            window.FindControl<StackPanel>("KeePassPanel")!.IsVisible.Should().BeFalse();
            window.FindControl<StackPanel>("BitwardenPanel")!.IsVisible.Should().BeFalse();
            window.FindControl<StackPanel>("TestConnectionPanel")!.IsVisible.Should().BeFalse();
        }
        finally { RestoreSettings(); }
    }

    [AvaloniaFact]
    public void ClickingCancel_ClosesWithoutTouchingSettings()
    {
        App.Settings.VaultProvider = "KeePass";
        try
        {
            var owner = new Window();
            owner.Show();

            var window = new VaultSettingsWindow();
            var closeTcs = new TaskCompletionSource();
            window.Closed += (_, _) => closeTcs.TrySetResult();
            window.Show(owner);

            // Change the selection without saving — Cancel must discard it.
            window.FindControl<ComboBox>("ProviderCombo")!.SelectedIndex = 2;

            var cancelButton = window.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == "Cancel");
            cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            closeTcs.Task.IsCompleted.Should().BeTrue();
            App.Settings.VaultProvider.Should().Be("KeePass", "Cancel must not persist the changed selection");
        }
        finally { RestoreSettings(); }
    }

    /// <summary>
    /// Real local HTTP server (no mocking) so Test Connection's diagnostic logic is verified against
    /// actual request/response bytes, matching this repo's general testing convention. Starts the
    /// listener first (so its port is known before the window/fields are configured), triggers Test
    /// Connection, and polls for a result.
    /// </summary>
    private static async Task<string> TestConnectionAgainstFakeServerAsync(
        int statusCode, string body, Action<VaultSettingsWindow, int> configure)
    {
        using var listener = new System.Net.HttpListener();
        var port = GetFreeTcpPort();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var serveTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = statusCode;
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var window = new VaultSettingsWindow();
        configure(window, port);

        var testButton = window.FindControl<Button>("TestConnectionButton")!;
        testButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var resultText = window.FindControl<TextBlock>("TestConnectionResult")!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (resultText.Text is null or "" or "Checking…" && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        await serveTask;
        listener.Stop();
        return resultText.Text ?? "";
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [AvaloniaFact]
    public async Task TestConnection_Bitwarden_ReportsSuccess_WhenServerReportsUnlocked()
    {
        var result = await TestConnectionAgainstFakeServerAsync(
            200, """{"data":{"template":{"status":"unlocked"}}}""",
            (window, port) =>
            {
                window.FindControl<ComboBox>("ProviderCombo")!.SelectedIndex = 2;
                window.FindControl<TextBox>("BitwardenUrlBox")!.Text = $"http://localhost:{port}";
            });

        result.Should().Contain("unlocked");
    }

    [AvaloniaFact]
    public async Task TestConnection_Bitwarden_ReportsLocked_WhenServerReportsLocked()
    {
        var result = await TestConnectionAgainstFakeServerAsync(
            200, """{"data":{"template":{"status":"locked"}}}""",
            (window, port) =>
            {
                window.FindControl<ComboBox>("ProviderCombo")!.SelectedIndex = 2;
                window.FindControl<TextBox>("BitwardenUrlBox")!.Text = $"http://localhost:{port}";
            });

        result.Should().Contain("locked").And.Contain("bw unlock", "the whole point of this diagnostic is telling the user exactly what to do next");
    }

    [AvaloniaFact]
    public async Task TestConnection_Bitwarden_ReportsUnreachable_WhenNothingIsListening()
    {
        var unusedPort = GetFreeTcpPort(); // freed immediately — guaranteed nothing is listening there
        var window = new VaultSettingsWindow();
        window.FindControl<ComboBox>("ProviderCombo")!.SelectedIndex = 2;
        window.FindControl<TextBox>("BitwardenUrlBox")!.Text = $"http://localhost:{unusedPort}";

        window.FindControl<Button>("TestConnectionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var resultText = window.FindControl<TextBlock>("TestConnectionResult")!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (resultText.Text is null or "" or "Checking…" && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        resultText.Text.Should().Contain("Not reachable");
    }

    [AvaloniaFact]
    public async Task TestConnection_KeePass_ReportsSuccess_WhenPingSucceeds()
    {
        var result = await TestConnectionAgainstFakeServerAsync(
            200, "",
            (window, port) =>
            {
                window.FindControl<ComboBox>("ProviderCombo")!.SelectedIndex = 1;
                window.FindControl<TextBox>("KeePassPortBox")!.Text = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            });

        result.Should().Contain("reachable");
    }
}
