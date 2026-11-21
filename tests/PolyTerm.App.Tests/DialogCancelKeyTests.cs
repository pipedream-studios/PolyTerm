using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using PolyTerm.App.Views;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Escape cancels every dialog that has a Cancel button, and closes the Macros window (issue #203).
/// Each of those buttons sets <c>IsCancel="True"</c>, so Escape fires the same handler a click would.
/// <see cref="QuickConnectWindow"/> isn't listed: it handles Escape itself in <c>OnKeyDown</c>.
/// </summary>
public sealed class DialogCancelKeyTests
{
    public static TheoryData<Type, string> DialogsWithCancelButton => new()
    {
        { typeof(NewConnectionWindow), "Cancel" },
        { typeof(EditFolderWindow), "Cancel" },
        { typeof(TerminalAppearanceWindow), "Cancel" },
        { typeof(CloseSessionDialog), "Cancel" },
        { typeof(DetachedSessionsDialog), "Cancel" },
        { typeof(KeePassPairingDialog), "Cancel" },
        { typeof(TunnelsWindow), "Cancel" },
        { typeof(RenameWindow), "Cancel" },
        { typeof(CredentialPickerDialog), "Cancel" },
        { typeof(VaultSettingsWindow), "Cancel" },
        { typeof(BitwardenLoginDialog), "Cancel" },
        { typeof(MacroEditDialog), "Cancel" },
        { typeof(TunnelEditDialog), "Cancel" },
        { typeof(ConfirmDialog), "Cancel" },
        { typeof(InputDialog), "Cancel" },
        { typeof(MacrosWindow), "Close" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(DialogsWithCancelButton))]
    public void CancelButton_IsTheWindowsCancelButton(Type dialogType, string buttonContent)
    {
        var dialog = (Window)Activator.CreateInstance(dialogType)!;
        dialog.Show();
        try
        {
            var cancel = dialog.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Content?.ToString() == buttonContent);
            cancel.IsCancel.Should().BeTrue($"Escape should cancel {dialogType.Name} the same way its Cancel button does");
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Escape_ClosesTheNewConnectionDialog()
    {
        var owner = new Window();
        owner.Show();
        var dialog = new NewConnectionWindow();
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        _ = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        closed.Should().BeTrue("Escape should cancel the New Connection dialog");
        owner.Close();
    }
}
