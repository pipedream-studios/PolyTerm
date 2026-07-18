using Avalonia.Controls;
using Avalonia.Interactivity;
using PolyTerm.Engine.Credentials;

namespace PolyTerm.App.Views;

/// <summary>
/// Collects a Bitwarden master password (plus email, if the vault isn't logged in at all yet) and
/// drives <see cref="BitwardenCliController"/> to log in or unlock, retrying in place if the account
/// turns out to need a two-factor code. Closes with <c>true</c> once login/unlock actually succeeded
/// and the managed <c>bw serve</c> process is up — issue #78's in-app replacement for "run 'bw login'/
/// 'bw unlock'/'bw serve' yourself in an external terminal".
/// </summary>
public sealed partial class BitwardenLoginDialog : Window
{
    private BitwardenCliController _controller = null!; // set by the real (non-XAML-loader) constructor below
    private bool _needsEmail;

    public BitwardenLoginDialog() => InitializeComponent();

    public BitwardenLoginDialog(BitwardenCliController controller, BitwardenAuthStatus initialStatus) : this()
    {
        _controller = controller;
        _needsEmail = initialStatus == BitwardenAuthStatus.Unauthenticated;

        EmailPanel.IsVisible = _needsEmail;
        HeadingText.Text = _needsEmail
            ? "Not logged in. Enter your Bitwarden account email and master password."
            : "Vault is locked. Enter your master password to unlock it.";
    }

    private async void Login_Click(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text ?? "";
        if (password.Length == 0)
        {
            ShowError("Enter your master password.");
            return;
        }
        if (_needsEmail && string.IsNullOrWhiteSpace(EmailBox.Text))
        {
            ShowError("Enter your account email.");
            return;
        }

        ErrorText.IsVisible = false; // clear any previous attempt's error before retrying
        SetBusy(true);
        try
        {
            var twoFactorCode = TwoFactorPanel.IsVisible ? TwoFactorBox.Text?.Trim() : null;

            var result = _needsEmail
                ? await _controller.LoginAsync(EmailBox.Text!.Trim(), password, twoFactorCode).ConfigureAwait(true)
                : await _controller.UnlockAsync(password).ConfigureAwait(true);

            if (result.Success)
            {
                Close(true);
                return;
            }

            if (result.TwoFactorRequired)
                TwoFactorPanel.IsVisible = true;

            ShowError(result.ErrorMessage ?? "Login failed.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Only toggles the busy/enabled state of the buttons - must NOT touch ErrorText.IsVisible here.
    /// This used to reset it to false unconditionally, which ran in Login_Click's `finally` block right
    /// after ShowError had just made it visible on the failure path, instantly hiding the very error it
    /// had just shown - the exact "no feedback on login or failure" bug reported after testing #78.
    /// </summary>
    private void SetBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
