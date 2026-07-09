namespace PolyTerm.Engine;

/// <summary>Persists the connection-profile tree (leaf profiles and folders) backing the connection browser.</summary>
public interface IProfileStore
{
    /// <summary>Returns every profile and folder, flat (not resolved, no inheritance applied).</summary>
    Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns a single profile as stored, or null if it doesn't exist. No inheritance applied.</summary>
    Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns the profile with inherited values resolved from ancestors. Ancestors are walked from
    /// the immediate parent up to the root, then applied outermost-first — so where more than one
    /// ancestor supplies a value for the same inherited property, the outermost ancestor wins, not
    /// the nearest parent. Throws if the profile doesn't exist.
    /// </summary>
    Task<ConnectionProfile> ResolveAsync(Guid id, CancellationToken ct = default);

    /// <summary>Inserts or updates a profile (upsert on <see cref="ConnectionProfile.Id"/>).</summary>
    Task<ConnectionProfile> SaveAsync(ConnectionProfile profile, CancellationToken ct = default);

    /// <summary>Deletes a profile or folder by id.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns the direct children of a folder (or root-level nodes when <paramref name="parentId"/> is null), unresolved.</summary>
    Task<IReadOnlyList<ConnectionProfile>> GetChildrenAsync(Guid? parentId, CancellationToken ct = default);
}
