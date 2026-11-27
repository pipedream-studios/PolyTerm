using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.App.Views;
using PolyTerm.Engine.Credentials;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>Covers how <see cref="BitwardenLoginDialog"/> opens for each session state.</summary>
public sealed class BitwardenLoginDialogTests
{
    /// <summary>
    /// Issue #384: the dialog is how to log in again from an unlocked session, so it mustn't call the vault locked.
    /// The email is already known, so only the password is asked for.
    /// </summary>
    [AvaloniaFact]
    public void WhenAlreadyUnlocked_OffersToLogInAgain_WithoutAskingForTheEmail()
    {
        using var client = new BitwardenApiClient(
            new BitwardenProviderOptions { BaseUrl = "http://localhost:1" },
            new OsKeychainProvider(NullLogger<OsKeychainProvider>.Instance),
            NullLogger<BitwardenApiClient>.Instance);

        var dialog = new BitwardenLoginDialog(client, BitwardenAuthStatus.Unlocked);

        dialog.FindControl<TextBlock>("HeadingText")!.Text.Should().Contain("already unlocked").And.Contain("log in again");
        dialog.FindControl<StackPanel>("EmailPanel")!.IsVisible.Should().BeFalse();
    }
}
