using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.Storage;

/// <summary>
/// SQLite-backed store of connection profiles, organized as a folder tree (via <c>parent_id</c>).
/// Each call opens and closes its own connection (<c>Pooling=False</c>), so instances are safe to
/// share across concurrent callers.
/// </summary>
internal sealed class ProfileStore : IProfileStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<ProfileStore> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Opens (creating if needed) the SQLite database at <paramref name="dbPath"/> and ensures the schema exists.</summary>
    public ProfileStore(string dbPath, ILogger<ProfileStore> logger)
    {
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False;";
        _logger = logger;
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS profiles (
                id          TEXT PRIMARY KEY,
                parent_id   TEXT,
                is_folder   INTEGER NOT NULL DEFAULT 0,
                name        TEXT NOT NULL,
                protocol_id TEXT NOT NULL DEFAULT '',
                host        TEXT,
                port        INTEGER,
                provider_id TEXT,
                item_ref    TEXT,
                settings    TEXT NOT NULL DEFAULT '{}',
                inherit     INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_profiles_parent ON profiles(parent_id);
            """;
        cmd.ExecuteNonQuery();
        _logger.LogDebug("Profile store schema ready.");
    }

    /// <summary>Returns every profile and folder in the store, ordered by name. Does not apply inheritance.</summary>
    public async Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM profiles ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await ReadProfilesAsync(reader, ct).ConfigureAwait(false);
    }

    /// <summary>Returns the raw stored profile by id, or null if not found. Does not apply inheritance.</summary>
    public async Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = await ReadProfilesAsync(reader, ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>
    /// Returns the profile with folder-tree inheritance applied (see <see cref="ApplyInheritanceAsync"/>).
    /// Skips the inheritance walk entirely (returning the raw stored profile) when the profile has
    /// no parent or its <c>Inherit</c> mask is <see cref="InheritanceMask.None"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No profile with <paramref name="id"/> exists.</exception>
    public async Task<ConnectionProfile> ResolveAsync(Guid id, CancellationToken ct = default)
    {
        var profile = await GetByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Profile {id} not found.");

        if (profile.Inherit == InheritanceMask.None || profile.ParentId is null)
            return profile;

        return await ApplyInheritanceAsync(profile, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills the profile's null <see cref="ConnectionProfile.Host"/>, <see cref="ConnectionProfile.Port"/>,
    /// and/or <see cref="ConnectionProfile.Credential"/> fields from ancestor folders, but only for the
    /// fields whose corresponding <see cref="InheritanceMask"/> flag is set on the profile, and only when
    /// the profile itself doesn't already have a value. Ancestors are walked from the profile's immediate
    /// parent up to the root, then applied in the opposite order (root/outermost first, descending toward
    /// the immediate parent) — so if more than one ancestor in the chain has a value set, the outermost
    /// one wins, not the nearest parent, since the field is left alone once a prior ancestor has filled it.
    /// The profile's own non-null values are never overwritten.
    /// </summary>
    private async Task<ConnectionProfile> ApplyInheritanceAsync(ConnectionProfile profile, CancellationToken ct)
    {
        // Walk up the tree collecting ancestors (closest first)
        var ancestors = new List<ConnectionProfile>();
        var current = profile;
        while (current.ParentId is Guid parentId)
        {
            var parent = await GetByIdAsync(parentId, ct).ConfigureAwait(false);
            if (parent is null) break;
            ancestors.Add(parent);
            current = parent;
        }

        // Apply from outermost ancestor inward, then overlay the profile
        foreach (var ancestor in Enumerable.Reverse(ancestors))
        {
            if (profile.Inherit.HasFlag(InheritanceMask.Host) && profile.Host is null)
                profile.Host = ancestor.Host;
            if (profile.Inherit.HasFlag(InheritanceMask.Port) && profile.Port is null)
                profile.Port = ancestor.Port;
            if (profile.Inherit.HasFlag(InheritanceMask.Credential) && profile.Credential is null)
                profile.Credential = ancestor.Credential;
        }

        return profile;
    }

    /// <summary>
    /// Inserts or updates (upsert by id) the profile, stamping <see cref="ConnectionProfile.UpdatedAt"/>
    /// with the current time as a side effect.
    /// </summary>
    public async Task<ConnectionProfile> SaveAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profiles
                (id, parent_id, is_folder, name, protocol_id, host, port,
                 provider_id, item_ref, settings, inherit, created_at, updated_at)
            VALUES
                ($id, $parent_id, $is_folder, $name, $protocol_id, $host, $port,
                 $provider_id, $item_ref, $settings, $inherit, $created_at, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                parent_id   = excluded.parent_id,
                is_folder   = excluded.is_folder,
                name        = excluded.name,
                protocol_id = excluded.protocol_id,
                host        = excluded.host,
                port        = excluded.port,
                provider_id = excluded.provider_id,
                item_ref    = excluded.item_ref,
                settings    = excluded.settings,
                inherit     = excluded.inherit,
                updated_at  = excluded.updated_at
            """;
        BindProfile(cmd, profile);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return profile;
    }

    /// <summary>
    /// Deletes the profile by id. No-op if it doesn't exist. Does not cascade to children —
    /// callers are responsible for handling any child profiles/folders under a deleted folder.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the direct children of a folder, ordered by name, or the top-level items when
    /// <paramref name="parentId"/> is null. Does not apply inheritance and does not recurse.
    /// </summary>
    public async Task<IReadOnlyList<ConnectionProfile>> GetChildrenAsync(Guid? parentId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        if (parentId is null)
        {
            cmd.CommandText = "SELECT * FROM profiles WHERE parent_id IS NULL ORDER BY name";
        }
        else
        {
            cmd.CommandText = "SELECT * FROM profiles WHERE parent_id = $pid ORDER BY name";
            cmd.Parameters.AddWithValue("$pid", parentId.Value.ToString());
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await ReadProfilesAsync(reader, ct).ConfigureAwait(false);
    }

    private static async Task<List<ConnectionProfile>> ReadProfilesAsync(SqliteDataReader reader, CancellationToken ct)
    {
        var result = new List<ConnectionProfile>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(MapRow(reader));
        }
        return result;
    }

    private static ConnectionProfile MapRow(SqliteDataReader r)
    {
        var providerId = r.IsDBNull(r.GetOrdinal("provider_id")) ? null : r.GetString(r.GetOrdinal("provider_id"));
        var itemRef    = r.IsDBNull(r.GetOrdinal("item_ref"))    ? null : r.GetString(r.GetOrdinal("item_ref"));

        return new ConnectionProfile
        {
            Id         = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
            ParentId   = r.IsDBNull(r.GetOrdinal("parent_id")) ? null : Guid.Parse(r.GetString(r.GetOrdinal("parent_id"))),
            IsFolder   = r.GetInt32(r.GetOrdinal("is_folder")) != 0,
            Name       = r.GetString(r.GetOrdinal("name")),
            ProtocolId = r.GetString(r.GetOrdinal("protocol_id")),
            Host       = r.IsDBNull(r.GetOrdinal("host")) ? null : r.GetString(r.GetOrdinal("host")),
            Port       = r.IsDBNull(r.GetOrdinal("port")) ? null : r.GetInt32(r.GetOrdinal("port")),
            Credential = (providerId is not null && itemRef is not null)
                ? new CredentialReference(providerId, itemRef)
                : null,
            Settings   = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                             r.GetString(r.GetOrdinal("settings"))) ?? [],
            Inherit    = (InheritanceMask)r.GetInt32(r.GetOrdinal("inherit")),
            CreatedAt  = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), System.Globalization.CultureInfo.InvariantCulture),
            UpdatedAt  = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at")), System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static void BindProfile(SqliteCommand cmd, ConnectionProfile p)
    {
        cmd.Parameters.AddWithValue("$id",          p.Id.ToString());
        cmd.Parameters.AddWithValue("$parent_id",   p.ParentId?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$is_folder",   p.IsFolder ? 1 : 0);
        cmd.Parameters.AddWithValue("$name",        p.Name);
        cmd.Parameters.AddWithValue("$protocol_id", p.ProtocolId);
        cmd.Parameters.AddWithValue("$host",        p.Host ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$port",        p.Port.HasValue ? (object)p.Port.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$provider_id", p.Credential?.ProviderId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$item_ref",    p.Credential?.ItemRef    ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$settings",    JsonSerializer.Serialize(p.Settings, JsonOpts));
        cmd.Parameters.AddWithValue("$inherit",     (int)p.Inherit);
        cmd.Parameters.AddWithValue("$created_at",  p.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated_at",  p.UpdatedAt.ToString("O"));
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>No-op: each operation opens and closes its own connection, so there is nothing to release here.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
