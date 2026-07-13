using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;

namespace CopilotScope.Collector.Persistence;

/// <summary>
/// Write-behind persistence: OTLP ingest marks sessions dirty, a background loop
/// flushes their snapshots to Postgres at most once per second, so bursts of
/// telemetry batches don't turn into a write storm. On startup it bootstraps the
/// schema and rehydrates the in-memory store, so a collector restart doesn't lose
/// session history. A Postgres outage degrades to in-memory-only (logged), it never
/// blocks ingest.
/// </summary>
public sealed class PersistenceWriter(
    SessionRepository repository,
    SessionStore store,
    QualityEngine quality,
    ILogger<PersistenceWriter> logger) : BackgroundService
{
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void MarkDirty(IEnumerable<string> sessionIds)
    {
        lock (_lock) foreach (var id in sessionIds) _dirty.Add(id);
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await repository.EnsureSchemaAsync(ct);
            var persisted = await repository.LoadAllAsync(limit: 200, ct);
            var restored = store.Rehydrate(persisted.Select(p => p.ToSession()));
            logger.LogInformation("Persistence ready — rehydrated {Count} session(s) from Postgres.", restored);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Postgres unavailable at startup — continuing in-memory only, will retry on writes.");
        }

        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }

            string[] ids;
            lock (_lock)
            {
                if (_dirty.Count == 0) continue;
                ids = _dirty.ToArray();
                _dirty.Clear();
            }

            foreach (var id in ids)
            {
                if (store.Get(id) is not { } session) continue;
                try
                {
                    var report = quality.Evaluate(session);
                    await repository.UpsertAsync(PersistedSession.From(session), report.Score, report.Grade, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to persist session {Id} — re-queueing.", id);
                    lock (_lock) _dirty.Add(id); // retry on next tick
                }
            }
        }
    }
}
