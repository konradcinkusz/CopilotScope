using System.Text.Json;
using Npgsql;

namespace CopilotScope.Collector.Persistence;

/// <summary>
/// Thin Npgsql repository — one table, jsonb snapshot per session, upserted by the
/// debounced <see cref="PersistenceWriter"/>. No EF: the access pattern is a pure
/// key/value upsert + full scan on startup, an ORM would only add weight.
/// </summary>
public sealed class SessionRepository(string connectionString) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS sessions (
                id            text PRIMARY KEY,
                first_seen    timestamptz NOT NULL,
                last_seen     timestamptz NOT NULL,
                quality_score double precision NOT NULL DEFAULT 0,
                quality_grade text NOT NULL DEFAULT '',
                snapshot      jsonb NOT NULL,
                updated_at    timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_last_seen ON sessions (last_seen DESC);
            """;
        await using var cmd = _dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(PersistedSession session, double qualityScore, string qualityGrade, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO sessions (id, first_seen, last_seen, quality_score, quality_grade, snapshot, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, now())
            ON CONFLICT (id) DO UPDATE SET
                last_seen = EXCLUDED.last_seen,
                quality_score = EXCLUDED.quality_score,
                quality_grade = EXCLUDED.quality_grade,
                snapshot = EXCLUDED.snapshot,
                updated_at = now();
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(session.Id);
        cmd.Parameters.AddWithValue(session.FirstSeen);
        cmd.Parameters.AddWithValue(session.LastSeen);
        cmd.Parameters.AddWithValue(qualityScore);
        cmd.Parameters.AddWithValue(qualityGrade);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
            Value = JsonSerializer.Serialize(session, Json)
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<PersistedSession>> LoadAllAsync(int limit, CancellationToken ct)
    {
        var result = new List<PersistedSession>();
        await using var cmd = _dataSource.CreateCommand(
            "SELECT snapshot FROM sessions ORDER BY last_seen DESC LIMIT $1;");
        cmd.Parameters.AddWithValue(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(0);
            if (JsonSerializer.Deserialize<PersistedSession>(json, Json) is { } snapshot)
                result.Add(snapshot);
        }
        return result;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("DELETE FROM sessions WHERE id = $1;");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Bulk-deletes rows whose id starts with the given prefix — used to clear a
    /// previously seeded demo/local dataset before writing a fresh one.</summary>
    public async Task<int> DeleteByPrefixAsync(string prefix, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("DELETE FROM sessions WHERE id LIKE $1;");
        cmd.Parameters.AddWithValue(prefix + "%");
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
