using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>SFTP file browser panel. Wires the view model's file-picker/dialog delegates to Avalonia's storage provider and shared dialogs.</summary>
public sealed partial class SftpBrowserView : UserControl
{
    public SftpBrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Binds the view model's UI-facing delegates: save/open file pickers (via the owning
    /// <see cref="TopLevel"/>'s storage provider), a name-prompt dialog (<see cref="InputDialog"/>),
    /// and a yes/no confirmation dialog (<see cref="ConfirmDialog"/>).
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not SftpBrowserViewModel vm) return;

        vm.PickSavePathAsync = async name =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return null;
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title             = "Download File",
                    SuggestedFileName = name,
                });
            return file?.Path.LocalPath;
        };

        vm.PickOpenPathsAsync = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return [];
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title         = "Upload Files",
                    AllowMultiple = true,
                });
            return files.Select(f => f.Path.LocalPath).ToList();
        };

        vm.PromptForNameAsync = async (title, initial) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return null;
            return await new InputDialog(title, initial).ShowDialog<string?>(owner);
        };

        vm.ConfirmAsync = async (title, message) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return false;
            return await new ConfirmDialog(title, message).ShowDialog<bool>(owner);
        };
    }

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SftpBrowserViewModel vm && vm.SelectedEntry is { } entry)
            _ = vm.OpenEntryAsync(entry);
    }

    private void PathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is SftpBrowserViewModel vm)
        {
            e.Handled = true;
            _ = vm.NavigateFromBarAsync(PathBox.Text ?? "/");
        }
    }

    private void MkDir_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SftpBrowserViewModel vm)
            _ = vm.MkDirAsync();
    }

    private void EntryEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SftpBrowserViewModel vm) return;
        if (TryGetContextEntry(sender) is { } entry)
            _ = vm.EditInLocalEditorAsync(entry);
    }

    private void EntryRename_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SftpBrowserViewModel vm) return;
        if (TryGetContextEntry(sender) is { } entry)
            _ = vm.RenameAsync(entry);
    }

    private void EntryDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SftpBrowserViewModel vm) return;
        if (TryGetContextEntry(sender) is { } entry)
            _ = vm.DeleteAsync(entry);
    }

    private void EntryChmod_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SftpBrowserViewModel vm) return;
        if (TryGetContextEntry(sender) is { } entry)
            _ = vm.ChmodAsync(entry);
    }

    /// <summary>Resolves the <see cref="RemoteEntryViewModel"/> targeted by a context-menu command (via the placement target's data context).</summary>
    private static RemoteEntryViewModel? TryGetContextEntry(object? sender) =>
        sender is MenuItem { Parent: ContextMenu cm } &&
        cm.PlacementTarget?.DataContext is RemoteEntryViewModel entry
            ? entry : null;
}
