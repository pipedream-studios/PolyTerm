namespace PolyTerm.Engine;

/// <summary>File transfer over an already-connected session (e.g. SFTP multiplexed over an SSH connection).</summary>
public interface IFileTransferChannel
{
    /// <summary>Remote working directory at connect time. "/" when unknown.</summary>
    string InitialDirectory { get; }

    /// <summary>Lists the entries of a remote directory.</summary>
    Task<IReadOnlyList<RemoteFileInfo>> ListAsync(string path, CancellationToken ct = default);

    /// <summary>Downloads a remote file into <paramref name="local"/>, reporting progress if requested.</summary>
    Task DownloadAsync(string remotePath, Stream local, IProgress<TransferProgress>? progress, CancellationToken ct = default);

    /// <summary>Uploads <paramref name="local"/> to a remote path, reporting progress if requested.</summary>
    Task UploadAsync(Stream local, string remotePath, IProgress<TransferProgress>? progress, CancellationToken ct = default);

    /// <summary>Deletes a remote file or empty directory.</summary>
    Task DeleteAsync(string remotePath, CancellationToken ct = default);

    /// <summary>Renames or moves a remote file or directory.</summary>
    Task RenameAsync(string sourcePath, string destinationPath, CancellationToken ct = default);

    /// <summary>Creates a remote directory.</summary>
    Task MkDirAsync(string path, CancellationToken ct = default);

    /// <summary>Returns metadata for a single remote path, or null if it doesn't exist.</summary>
    Task<RemoteFileInfo?> StatAsync(string path, CancellationToken ct = default);

    /// <summary>Set POSIX permissions (e.g., "755"). No-op on protocols that don't support it.</summary>
    Task ChmodAsync(string path, string permissions, CancellationToken ct = default);
}
