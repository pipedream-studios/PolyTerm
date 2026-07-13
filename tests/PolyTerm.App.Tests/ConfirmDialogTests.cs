using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AwesomeAssertions;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.App.Views;

namespace PolyTerm.App.Tests;

/// <summary>
/// Spike for <c>Avalonia.Headless</c> (docs/testing.md §2/§4): can real dialog logic — construction,
/// data binding, and an actual simulated button click driving <c>ShowDialog</c>'s result — be tested
/// without a real window, the way the M3 punch list (Tunnels editor, Macros CRUD) would need?
/// Answer: yes. This is the first test in the repo using this package.
/// </summary>
public sealed class ConfirmDialogTests
{
    [AvaloniaFact]
    public void Constructor_SetsTitleAndMessage()
    {
        var dialog = new ConfirmDialog("Delete file?", "Really delete /etc/passwd?");

        dialog.Title.Should().Be("Delete file?");
        var messageLabel = dialog.FindControl<TextBlock>("MessageLabel");
        messageLabel.Should().NotBeNull();
        messageLabel!.Text.Should().Be("Really delete /etc/passwd?");
    }

    [AvaloniaFact]
    public async Task ClickingActionButton_ClosesDialogWithTrue()
    {
        var owner = new Window();
        owner.Show();

        var dialog = new ConfirmDialog("Confirm", "Proceed?");
        var resultTask = dialog.ShowDialog<bool>(owner);

        var actionButton = dialog.FindControl<Button>("ActionButton");
        actionButton.Should().NotBeNull();
        actionButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var result = await resultTask;
        result.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ClickingCancelButton_ClosesDialogWithFalse()
    {
        var owner = new Window();
        owner.Show();

        var dialog = new ConfirmDialog("Confirm", "Proceed?");
        var resultTask = dialog.ShowDialog<bool>(owner);

        // Cancel has no x:Name in the XAML — find it by content instead.
        var cancelButton = dialog.GetVisualDescendants().OfType<Button>()
            .First(b => b.Content?.ToString() == "Cancel");
        cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var result = await resultTask;
        result.Should().BeFalse();
    }

    /// <summary>
    /// Default constructor call (2 args) preserves the dialog's original behavior exactly — SFTP's
    /// delete-confirmation call site (<c>SftpBrowserView</c>) doesn't pass <c>actionLabel</c>/
    /// <c>destructive</c> at all, so it must keep getting "Delete"/red without any changes on its side.
    /// </summary>
    [AvaloniaFact]
    public void DefaultConstructor_StillProducesDestructiveDeleteButton()
    {
        var dialog = new ConfirmDialog("Delete file?", "Really delete /etc/passwd?");

        var actionButton = dialog.FindControl<Button>("ActionButton");

        actionButton!.Content.Should().Be("Delete");
        actionButton.Background.Should().NotBeNull("the destructive/red styling should still apply by default");
    }

    /// <summary>
    /// Fixes a real, small bug this spike originally surfaced: the action button used to be
    /// unconditionally "Delete"/red regardless of caller, so the MCP approval-gate prompt showed a
    /// red "Delete" button even when approving a non-destructive action like terminal_execute.
    /// <see cref="ApprovalGateService"/> now passes an action-appropriate label and
    /// <c>destructive: false</c> for everything except <c>SftpDelete</c>.
    /// </summary>
    [AvaloniaFact]
    public void NonDestructiveLabel_ChangesContentAndClearsDestructiveStyling()
    {
        var redBackground = new ConfirmDialog().FindControl<Button>("ActionButton")!.Background;

        var dialog = new ConfirmDialog("MCP approval required", "Run a command?", actionLabel: "Run", destructive: false);
        var actionButton = dialog.FindControl<Button>("ActionButton");

        actionButton!.Content.Should().Be("Run");
        actionButton.Background.Should().NotBe(redBackground, "a non-destructive action shouldn't keep the red delete-button styling");
    }
}
