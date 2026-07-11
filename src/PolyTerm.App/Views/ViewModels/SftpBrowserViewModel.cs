using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>
/// Backs the SFTP file browser panel shown alongside a terminal session: lists the current remote
/// directory and drives navigation, download, upload, rename, delete, mkdir, and edit-in-local-editor
/// over an <see cref="IFileTransferChannel"/>.
/// </summary>
public sealed partial class SftpBrowserViewModel : ViewModelBase, IDisposable
{
    private readonly IFileTransferChannel _channel;

    // One entry per file currently open in a local editor: watches the downloaded temp copy and
    // re-uploads on save. Keyed by the local temp path (unique per edit session, even for the same
    // remote file edited twice) so each has its own independent watcher/re-upload state.
    private readonly Dictionary<string, EditSession> _editSessions = [];
    private readonly object _editSessionsLock = new();
    private bool _disposed;

    [ObservableProperty] private string _currentPath = "/";

    [ObservableProperty]
    private ObservableCollection<RemoteEntryViewModel> _entries = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChmodSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedCommand))]
    private RemoteEntryViewModel? _selectedEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
    private bool _isTransferring;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _transferStatus;

    public string? ParentPath => ComputeParent(CurrentPath);

    // Set by SftpBrowserView.OnDataContextChanged to avoid pulling StorageProvider into the VM
    /// <summary>Prompts the user for a local save path for downloading <c>entry name</c>; returns null if cancelled.</summary>
    public Func<string, Task<string?>>?                 PickSavePathAsync  { get; set; }
    /// <summary>Prompts the user to pick one or more local files to upload; returns an empty list if cancelled.</summary>
    public Func<Task<IReadOnlyList<string>>>?           PickOpenPathsAsync { get; set; }
    /// <summary>Prompts the user for a name (rename or new-folder); args are (title, initial value); returns null if cancelled.</summary>
    public Func<string, string, Task<string?>>?         PromptForNameAsync { get; set; }
    /// <summary>Prompts the user to confirm a destructive action; args are (title, message); returns true if confirmed.</summary>
    public Func<string, string, Task<bool>>?            ConfirmAsync       { get; set; }
    /// <summary>Launches a local file with the OS's default handler for <see cref="EditInLocalEditorAsync"/>. Overridable for testing — defaults to a real <see cref="Process.Start(ProcessStartInfo)"/>.</summary>
    public Action<string> LaunchEditor { get; set; } =
        path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Creates the browser and kicks off an initial navigation to <paramref name="channel"/>'s <c>InitialDirectory</c> (or "/" if unset).</summary>
    public SftpBrowserViewModel(IFileTransferChannel channel)
    {
        _channel = channel;
        var start = string.IsNullOrEmpty(channel.InitialDirectory) ? "/" : channel.InitialDirectory;
        _ = NavigateAsync(start);
    }

    /// <summary>Lists <paramref name="path"/> on the remote host and replaces <see cref="Entries"/> with the result.</summary>
    [RelayCommand]
    private async Task NavigateAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error  = null;
        try
        {
            var infos = await _channel.ListAsync(path).ConfigureAwait(false);
            var vms   = infos
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new RemoteEntryViewModel(e))
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentPath   = path;
                Entries       = new ObservableCollection<RemoteEntryViewModel>(vms);
                SelectedEntry = null;
                OnPropertyChanged(nameof(ParentPath));
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Error = ex.Message);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private Task NavigateUpAsync()
    {
        var parent = ComputeParent(CurrentPath);
        return parent is not null ? NavigateAsync(parent) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task RefreshAsync() => NavigateAsync(CurrentPath);

    /// <summary>Navigates into <paramref name="entry"/> if it's a directory; no-op for files (use <see cref="DownloadAsync"/> instead).</summary>
    public Task OpenEntryAsync(RemoteEntryViewModel entry) =>
        entry.IsDirectory ? NavigateAsync(entry.FullPath) : Task.CompletedTask;

    /// <summary>Downloads <see cref="SelectedEntry"/> to a path chosen via <see cref="PickSavePathAsync"/>, reporting progress through <see cref="TransferStatus"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        var entry = SelectedEntry;
        if (entry is null || entry.IsDirectory || PickSavePathAsync is null) return;

        var localPath = await PickSavePathAsync(entry.Name);
        if (localPath is null) return;

        IsTransferring = true;
        TransferStatus = $"Downloading {entry.Name}…";
        Error          = null;
        try
        {
            var progress = new Progress<TransferProgress>(p =>
                TransferStatus = p.TotalBytes.HasValue
                    ? $"Downloading {entry.Name} ({p.BytesTransferred * 100 / p.TotalBytes.Value}%)"
                    : $"Downloading {entry.Name} ({p.BytesTransferred:N0} B)");

            await using var fs = File.Create(localPath);
            await _channel.DownloadAsync(entry.FullPath, fs, progress).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => TransferStatus = $"Downloaded {entry.Name}");
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Error          = $"Download failed: {ex.Message}";
                TransferStatus = null;
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsTransferring = false);
        }
    }
    private bool CanDownload() => SelectedEntry is { IsDirectory: false } && !IsTransferring;

    /// <summary>Uploads one or more local files (chosen via <see cref="PickOpenPathsAsync"/>) into <see cref="CurrentPath"/>, then refreshes the listing.</summary>
    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadAsync()
    {
        if (PickOpenPathsAsync is null) return;
        var paths = await PickOpenPathsAsync();
        if (paths.Count == 0) return;

        IsTransferring = true;
        Error          = null;
        try
        {
            foreach (var localPath in paths)
            {
                var fileName   = Path.GetFileName(localPath);
                var remotePath = CurrentPath.TrimEnd('/') + "/" + fileName;

                await Dispatcher.UIThread.InvokeAsync(() => TransferStatus = $"Uploading {fileName}…");

                var progress = new Progress<TransferProgress>(p =>
                    TransferStatus = p.TotalBytes.HasValue
                        ? $"Uploading {fileName} ({p.BytesTransferred * 100 / p.TotalBytes.Value}%)"
                        : $"Uploading {fileName} ({p.BytesTransferred:N0} B)");

                await using var fs = File.OpenRead(localPath);
                await _channel.UploadAsync(fs, remotePath, progress).ConfigureAwait(false);
            }

            var msg = $"Uploaded {paths.Count} file{(paths.Count == 1 ? "" : "s")}";
            await Dispatcher.UIThread.InvokeAsync(() => TransferStatus = msg);
            await NavigateAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Error          = $"Upload failed: {ex.Message}";
                TransferStatus = null;
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsTransferring = false);
        }
    }
    private bool CanUpload() => !IsTransferring;

    /// <summary>Prompts for a new name via <see cref="PromptForNameAsync"/>, renames <paramref name="entry"/> in place, and refreshes the listing.</summary>
    public async Task RenameAsync(RemoteEntryViewModel entry)
    {
        if (PromptForNameAsync is null) return;
        var newName = await PromptForNameAsync("Rename", entry.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name) return;

        var parent  = ComputeParent(entry.FullPath) ?? "/";
        var newPath = parent.TrimEnd('/') + "/" + newName;

        IsBusy = true;
        Error  = null;
        try
        {
            await _channel.RenameAsync(entry.FullPath, newPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Error = $"Rename failed: {ex.Message}");
            return;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }

        await NavigateAsync(CurrentPath);
    }

    /// <summary>Confirms via <see cref="ConfirmAsync"/>, then deletes <paramref name="entry"/> and refreshes the listing.</summary>
    public async Task DeleteAsync(RemoteEntryViewModel entry)
    {
        if (ConfirmAsync is null) return;
        var confirmed = await ConfirmAsync("Confirm Delete", $"Delete '{entry.Name}'? This cannot be undone.").ConfigureAwait(true);
        if (!confirmed) return;

        IsBusy = true;
        Error  = null;
        try
        {
            await _channel.DeleteAsync(entry.FullPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Error = $"Delete failed: {ex.Message}");
            return;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }

        await NavigateAsync(CurrentPath);
    }

    /// <summary>Prompts for octal permissions (e.g. "755") via <see cref="PromptForNameAsync"/>, chmods <paramref name="entry"/>, and refreshes the listing.</summary>
    public async Task ChmodAsync(RemoteEntryViewModel entry)
    {
        if (PromptForNameAsync is null) return;
        var initial = entry.IsDirectory ? "755" : "644";
        var input = await PromptForNameAsync("Permissions (octal, e.g. 755)", initial).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(input)) return;

        if (input.Length is < 3 or > 4 || input.Any(c => c is < '0' or > '7'))
        {
            await Dispatcher.UIThread.InvokeAsync(() => Error = $"Invalid permissions '{input}' — expected an octal mode like 755.");
            return;
        }

        IsBusy = true;
        Error  = null;
        try
        {
            await _channel.ChmodAsync(entry.FullPath, input).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Error = $"Change permissions failed: {ex.Message}");
            return;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }

        await NavigateAsync(CurrentPath);
    }

    /// <summary>
    /// Toolbar-facing counterparts of <see cref="RenameAsync"/>/<see cref="ChmodAsync"/>/
    /// <see cref="DeleteAsync"/>/<see cref="EditInLocalEditorAsync"/> that act on <see cref="SelectedEntry"/>
    /// directly, rather than requiring the row's <c>ContextMenu</c>. Added because that context menu has
    /// no keyboard-accessible way to open it at all in this Avalonia version — see #25 — so these are the
    /// only way a keyboard-only user (or anyone without a working right-click) can reach these actions.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RenameSelectedAsync() => SelectedEntry is { } entry ? RenameAsync(entry) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ChmodSelectedAsync() => SelectedEntry is { } entry ? ChmodAsync(entry) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteSelectedAsync() => SelectedEntry is { } entry ? DeleteAsync(entry) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private Task EditSelectedAsync() => SelectedEntry is { } entry ? EditInLocalEditorAsync(entry) : Task.CompletedTask;

    private bool HasSelection() => SelectedEntry is not null;
    private bool CanEditSelection() => SelectedEntry is { IsDirectory: false };

    /// <summary>Prompts for a folder name via <see cref="PromptForNameAsync"/>, creates it under <see cref="CurrentPath"/>, and refreshes the listing.</summary>
    public async Task MkDirAsync()
    {
        if (PromptForNameAsync is null) return;
        var name = await PromptForNameAsync("New Folder", "").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;

        var newPath = CurrentPath.TrimEnd('/') + "/" + name;
        IsBusy = true;
        Error  = null;
        try
        {
            await _channel.MkDirAsync(newPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Error = $"Create folder failed: {ex.Message}");
            return;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }

        await NavigateAsync(CurrentPath);
    }

    /// <summary>
    /// Downloads <paramref name="entry"/> to a fresh temp file, opens it with the OS's default handler
    /// for that file type, then watches the temp file and re-uploads it to <see cref="RemoteEntryViewModel.FullPath"/>
    /// on every save — the same "edit remotely via a local app" workflow WinSCP/FileZilla call "Edit".
    /// One <see cref="EditSession"/> per call (own temp dir, own watcher), so editing the same remote
    /// file twice doesn't collide. The session stays open (watching, re-uploading on save) until this
    /// view model is disposed — there's no "stop editing" action; closing the editor app just means
    /// future saves never happen, same as closing any file without further edits.
    /// </summary>
    public async Task EditInLocalEditorAsync(RemoteEntryViewModel entry)
    {
        if (entry.IsDirectory) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "polyterm-edit", Guid.NewGuid().ToString("N"));
        string localPath;
        try
        {
            Directory.CreateDirectory(tempDir);
            localPath = Path.Combine(tempDir, entry.Name);

            IsBusy = true;
            Error  = null;
            await using (var fs = File.Create(localPath))
                await _channel.DownloadAsync(entry.FullPath, fs, progress: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Error = $"Couldn't open '{entry.Name}' for editing: {ex.Message}");
            return;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }

        var session = new EditSession(localPath, entry.FullPath, entry.Name, _channel,
            status => Dispatcher.UIThread.Post(() => TransferStatus = status),
            error  => Dispatcher.UIThread.Post(() => Error = error));

        lock (_editSessionsLock)
        {
            if (_disposed) { session.Dispose(); return; }
            _editSessions[localPath] = session;
        }

        try
        {
            LaunchEditor(localPath);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                Error = $"Downloaded '{entry.Name}' but couldn't launch an editor for it: {ex.Message}");
        }
    }

    /// <summary>Stops watching every open edit session and best-effort deletes their temp files. Safe to call more than once.</summary>
    public void Dispose()
    {
        List<EditSession> sessions;
        lock (_editSessionsLock)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = [.. _editSessions.Values];
            _editSessions.Clear();
        }
        foreach (var session in sessions)
            session.Dispose();
    }

    /// <summary>
    /// Watches one downloaded temp file for saves (debounced — editors often write a file multiple
    /// times per save, e.g. a temp-then-rename pattern) and re-uploads it to <paramref name="remotePath"/>
    /// each time. Re-upload failures are reported via <paramref name="onError"/> rather than thrown —
    /// there's no caller left awaiting this by the time a save happens.
    /// </summary>
    private sealed class EditSession : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly System.Timers.Timer _debounce;
        private readonly string _localPath;
        private readonly string _remotePath;
        private readonly string _fileName;
        private readonly IFileTransferChannel _channel;
        private readonly Action<string> _onStatus;
        private readonly Action<string> _onError;
        private int _uploadInFlight;
        private bool _disposed;

        public EditSession(string localPath, string remotePath, string fileName, IFileTransferChannel channel,
            Action<string> onStatus, Action<string> onError)
        {
            _localPath  = localPath;
            _remotePath = remotePath;
            _fileName   = fileName;
            _channel    = channel;
            _onStatus   = onStatus;
            _onError    = onError;

            _debounce = new System.Timers.Timer(500) { AutoReset = false };
            _debounce.Elapsed += (_, _) => _ = ReuploadAsync();

            _watcher = new FileSystemWatcher(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => { _debounce.Stop(); _debounce.Start(); };
            _watcher.EnableRaisingEvents = true;
        }

        private async Task ReuploadAsync()
        {
            // A debounced save can still overlap a slow upload of the previous save — skip rather
            // than queue, since only the latest content on disk matters.
            if (Interlocked.Exchange(ref _uploadInFlight, 1) == 1) return;
            try
            {
                _onStatus($"Saving {_fileName} to remote…");
                await using var fs = File.OpenRead(_localPath);
                await _channel.UploadAsync(fs, _remotePath, progress: null).ConfigureAwait(false);
                _onStatus($"Saved {_fileName}");
            }
            catch (Exception ex)
            {
                _onError($"Couldn't save '{_fileName}' back to the remote: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _uploadInFlight, 0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watcher.Dispose();
            _debounce.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(_localPath)!, recursive: true); }
            catch { /* best effort — editor may still have the file open/locked */ }
        }
    }

    // Called from code-behind PathBox on Enter — navigates to whatever the user typed
    public Task NavigateFromBarAsync(string path)
    {
        path = path.Trim();
        return string.IsNullOrEmpty(path) ? Task.CompletedTask : NavigateAsync(path);
    }

    private static string? ComputeParent(string path)
    {
        path = path.TrimEnd('/');
        if (string.IsNullOrEmpty(path) || path == "/") return null;
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }
}
