using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>
/// Read-only display wrapper around a single <see cref="RemoteFileInfo"/> entry, used by
/// <see cref="SftpBrowserViewModel"/> to render one row (file or directory) in the SFTP browser.
/// </summary>
public sealed class RemoteEntryViewModel
{
    private readonly RemoteFileInfo _info;

    public RemoteEntryViewModel(RemoteFileInfo info) => _info = info;

    public string Name         => _info.Name;
    public string FullPath     => _info.FullPath;
    public bool   IsDirectory  => _info.IsDirectory;
    public string Icon         => _info.IsDirectory ? "📁" : "📄";
    public string SizeText     => _info.IsDirectory ? "" : FormatBytes(_info.Size);
    public string LastModifiedText => _info.LastModified.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    public string? Permissions => _info.Permissions;

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024                 => $"{bytes} B",
        < 1024 * 1024          => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024  => $"{bytes / (1024.0 * 1024):F1} MB",
        _                      => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
    };
}
