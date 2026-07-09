namespace PolyTerm.Engine;

/// <summary>Metadata for a single remote file or directory, as returned by <see cref="IFileTransferChannel"/>.</summary>
public sealed class RemoteFileInfo
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTimeOffset LastModified { get; init; }

    /// <summary>POSIX permission string (e.g. "755"), or null on protocols that don't expose it.</summary>
    public string? Permissions { get; init; }
    public string? Owner { get; init; }
    public string? Group { get; init; }
}
