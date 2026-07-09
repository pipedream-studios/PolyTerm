using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PolyTerm.Engine.Storage;

/// <summary>
/// SQLite-backed store of trusted host keys, keyed by (host, port, algorithm). Backs the
/// TOFU (trust-on-first-use) host key policy. Each call opens and closes its own connection
/// (<c>Pooling=False</c>), so instances are safe to share across concurrent callers.
/// </summary>
internal sealed class HostKeyStore : IHostKeyStore
{
    private readonly string _connectionString;
    private readonly ILogger<HostKeyStore> _logger;

    /// <summary>Opens (creating if needed) the SQLite database at <paramref name="dbPath"/> and ensures the schema exists.</summary>
    public HostKeyStore(string dbPath, ILogger<HostKeyStore> logger)
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
            CREATE TABLE IF NOT EXISTS host_keys (
                host        TEXT NOT NULL,
                port        INTEGER NOT NULL,
                algorithm   TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                first_seen  TEXT NOT NULL,
                last_seen   TEXT NOT NULL,
                PRIMARY KEY (host, port, algorithm)
            );
            """;
        cmd.ExecuteNonQuery();
        _logger.LogDebug("Host key store schema ready.");
    }

    /// <summary>Looks up the trusted key record for a host/port/algorithm, or null if none is recorded.</summary>
    public async Task<HostKeyRecord?> FindAsync(string host, int port, string algorithm, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM host_keys WHERE host=$h AND port=$p AND algorithm=$a";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$p", port);
        cmd.Parameters.AddWithValue("$a", algorithm);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return MapRow(r);
    }

    /// <summary>
    /// Records (or, if a record for this host/port/algorithm already exists, updates) the
    /// fingerprint and last-seen timestamp for a trusted key.
    /// </summary>
    public async Task TrustAsync(HostKeyInfo key, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        var now = DateTimeOffset.UtcNow.ToString("O");
        cmd.CommandText = """
            INSERT INTO host_keys (host, port, algorithm, fingerprint, first_seen, last_seen)
            VALUES ($h, $p, $a, $fp, $fs, $ls)
            ON CONFLICT(host, port, algorithm) DO UPDATE SET
                fingerprint = excluded.fingerprint,
                last_seen   = excluded.last_seen
            """;
        cmd.Parameters.AddWithValue("$h",  key.Host);
        cmd.Parameters.AddWithValue("$p",  key.Port);
        cmd.Parameters.AddWithValue("$a",  key.Algorithm);
        cmd.Parameters.AddWithValue("$fp", key.FingerprintSha256);
        cmd.Parameters.AddWithValue("$fs", now);
        cmd.Parameters.AddWithValue("$ls", now);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Trusted host key {Alg}/{Fp} for {Host}:{Port}", key.Algorithm, key.FingerprintSha256, key.Host, key.Port);
    }

    /// <summary>Returns every trusted host key record, ordered by host then port.</summary>
    public async Task<IReadOnlyList<HostKeyRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM host_keys ORDER BY host, port";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<HostKeyRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            results.Add(MapRow(r));
        return results;
    }

    /// <summary>Removes all trusted key records for a host/port (across all algorithms). No-op if none exist.</summary>
    public async Task RemoveAsync(string host, int port, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM host_keys WHERE host=$h AND port=$p";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$p", port);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static HostKeyRecord MapRow(SqliteDataReader r) => new()
    {
        Host               = r.GetString(0),
        Port               = r.GetInt32(1),
        Algorithm          = r.GetString(2),
        FingerprintSha256  = r.GetString(3),
        FirstSeenAt        = DateTimeOffset.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
        LastSeenAt         = DateTimeOffset.Parse(r.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
    };

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
