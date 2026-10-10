using Avalonia.Controls;
using Avalonia.Interactivity;
using PolyTerm.Engine;

namespace PolyTerm.App.Views;

/// <summary>
/// Modal dialog shown when a host presents a different SSH key than the one on record. Closes with a
/// <see cref="HostKeyChangeChoice"/>; closing the window any other way means <see cref="HostKeyChangeChoice.Disconnect"/>.
/// </summary>
public sealed partial class HostKeyChangeDialog : Window
{
    public HostKeyChangeDialog() => InitializeComponent();

    public HostKeyChangeDialog(HostKeyChangeInfo info) : this()
    {
        HeadlineLabel.Text       = $"The host key for {info.Host}:{info.Port} has changed ({info.Algorithm}).";
        KnownHeaderLabel.Text    = $"Known key (first seen {info.KnownFirstSeenAt.LocalDateTime:g})";
        KnownFingerprintBox.Text = $"SHA256:{info.KnownFingerprintSha256}";
        NewFingerprintBox.Text   = $"SHA256:{info.NewFingerprintSha256}";
    }

    private void TrustOnce_Click(object? sender, RoutedEventArgs e)        => Close(HostKeyChangeChoice.TrustOnce);
    private void TrustPermanently_Click(object? sender, RoutedEventArgs e) => Close(HostKeyChangeChoice.TrustPermanently);
    private void Disconnect_Click(object? sender, RoutedEventArgs e)       => Close(HostKeyChangeChoice.Disconnect);
}
