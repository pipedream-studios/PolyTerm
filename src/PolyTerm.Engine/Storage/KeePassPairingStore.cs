using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.Storage;

/// <summary>A completed KeePassRPC pairing — the client identity KeePassRPC keys its stored key by, and the SRP-derived shared key itself.</summary>
public sealed record KeePassPairingRecord(string ClientId, string SharedKeyHex, DateTimeOffset PairedAt);

/// <summary>
/// SQLite-backed store for the single KeePassRPC pairing this MVP supports (one KeePass instance, not
/// per-database) — same TOFU-record shape as <see cref="HostKeyStore"/>, just a single row rather than
/// keyed by host. Each call opens and closes its own connection (<c>Pooling=False</c>), so instances are
/// safe to share across concurrent callers.
/// </summary>
public sealed class KeePassPairingStore
{
    private const int SingletonRowId = 1;
    private readonly string _connectionString;
    private readonly ILogger<KeePassPairingStore> _logger;

    /// <summary>Opens (creating if needed) the SQLite database at <paramref name="dbPath"/> and ensures the schema exists.</summary>
    public KeePassPairingStore(string dbPath, ILogger<KeePassPairingStore> logger)
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
            CREATE TABLE IF NOT EXISTS keepass_pairing (
                id             INTEGER PRIMARY KEY,
                client_id      TEXT NOT NULL,
                shared_key_hex TEXT NOT NULL,
                paired_at      TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        _logger.LogDebug("KeePass pairing store schema ready.");
    }

    /// <summary>Returns the current pairing, or null if KeePass has never been paired (or was forgotten via <see cref="RemoveAsync"/>).</summary>
    public async Task<KeePassPairingRecord?> FindAsync(CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id, shared_key_hex, paired_at FROM keepass_pairing WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", SingletonRowId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new KeePassPairingRecord(
            r.GetString(0),
            r.GetString(1),
            DateTimeOffset.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Persists a successful pairing, replacing any previous one.</summary>
    public async Task SaveAsync(KeePassPairingRecord record, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO keepass_pairing (id, client_id, shared_key_hex, paired_at)
            VALUES ($id, $cid, $key, $at)
            ON CONFLICT(id) DO UPDATE SET
                client_id      = excluded.client_id,
                shared_key_hex = excluded.shared_key_hex,
                paired_at      = excluded.paired_at
            """;
        cmd.Parameters.AddWithValue("$id", SingletonRowId);
        cmd.Parameters.AddWithValue("$cid", record.ClientId);
        cmd.Parameters.AddWithValue("$key", record.SharedKeyHex);
        cmd.Parameters.AddWithValue("$at", record.PairedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Saved KeePassRPC pairing for client {ClientId}.", record.ClientId);
    }

    /// <summary>Forgets the current pairing ("Forget Pairing" in the UI). No-op if not currently paired.</summary>
    public async Task RemoveAsync(CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM keepass_pairing WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", SingletonRowId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
