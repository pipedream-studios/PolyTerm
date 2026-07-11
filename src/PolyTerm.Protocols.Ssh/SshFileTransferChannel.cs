using Renci.SshNet;
using Renci.SshNet.Sftp;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Ssh;

/// <summary>File-transfer channel backed by an already-connected SFTP client, owned by the parent <see cref="SshSession"/>.</summary>
internal sealed class SshFileTransferChannel : IFileTransferChannel
{
    private readonly SftpClient _sftp;

    public SshFileTransferChannel(SftpClient sftp) => _sftp = sftp;

    public string InitialDirectory => string.IsNullOrEmpty(_sftp.WorkingDirectory) ? "/" : _sftp.WorkingDirectory;

    /// <summary>Lists directory contents, excluding the <c>.</c> and <c>..</c> entries.</summary>
    public async Task<IReadOnlyList<RemoteFileInfo>> ListAsync(string path, CancellationToken ct = default)
    {
        var entries = await Task.Run(() => _sftp.ListDirectory(path), ct).ConfigureAwait(false);
        return entries
            .Where(e => e.Name != "." && e.Name != "..")
            .Select(MapEntry)
            .ToList();
    }

    /// <summary>Downloads a remote file into <paramref name="local"/>, reporting byte-level progress if requested.</summary>
    public async Task DownloadAsync(string remotePath, Stream local, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            _sftp.DownloadFile(remotePath, local, transferred =>
                progress?.Report(new TransferProgress { BytesTransferred = (long)transferred, CurrentPath = remotePath }));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Uploads <paramref name="local"/> to the remote path, overwriting any existing file, reporting byte-level progress if requested.</summary>
    public async Task UploadAsync(Stream local, string remotePath, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            _sftp.UploadFile(local, remotePath, true, transferred =>
                progress?.Report(new TransferProgress { BytesTransferred = (long)transferred, CurrentPath = remotePath }));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a remote file or (empty) directory.</summary>
    public Task DeleteAsync(string remotePath, CancellationToken ct = default) =>
        Task.Run(() => _sftp.Delete(remotePath), ct);

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken ct = default) =>
        Task.Run(() => _sftp.RenameFile(sourcePath, destinationPath), ct);

    public Task MkDirAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => _sftp.CreateDirectory(path), ct);

    /// <summary>Returns metadata for the given path, or <c>null</c> if it does not exist.</summary>
    public async Task<RemoteFileInfo?> StatAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var attr = await Task.Run(() => _sftp.Get(path), ct).ConfigureAwait(false);
            return MapEntry(attr);
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Changes permissions on a remote path. <paramref name="permissions"/> is a POSIX octal mode string
    /// (e.g. <c>"755"</c>).
    /// </summary>
    /// <remarks>
    /// Deliberately parsed as plain decimal, not octal - confirmed live this is what SSH.NET's own
    /// <see cref="Renci.SshNet.SftpClient.ChangePermissions"/> actually expects: it re-interprets the
    /// passed integer's own decimal digit sequence as octal internally, so the caller is meant to hand
    /// it the human-typed mode's digits as a plain number (<c>ChangePermissions(path, 640)</c>), not a
    /// pre-converted octal-to-decimal value. Pre-converting here (as this used to do, via
    /// <c>Convert.ToUInt16(permissions, 8)</c>) double-converts: e.g. "640" → decimal 416 → SSH.NET
    /// treats "416" as octal again → the file ends up at mode 416, not 640. Caught by a real end-to-end
    /// test against the real SFTP server (<c>SftpChmodViaToolbarButton_ChangesRealPermissionsOnTheServer</c>
    /// in the FlaUI suite), not assumed from documentation - <c>ChangePermissions</c>'s own XML doc
    /// comment says only "the mode", no format detail.
    /// </remarks>
    public Task ChmodAsync(string path, string permissions, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var mode = Convert.ToUInt16(permissions, 10);
            _sftp.ChangePermissions(path, (short)mode);
        }, ct);

    private static RemoteFileInfo MapEntry(ISftpFile e) => new()
    {
        Name         = e.Name,
        FullPath     = e.FullName,
        IsDirectory  = e.IsDirectory,
        Size         = e.Length,
        LastModified = new DateTimeOffset(e.LastWriteTime),
        Permissions  = e.IsDirectory ? "drwxr-xr-x" : "-rw-r--r--",
        Owner        = e.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Group        = e.GroupId.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
