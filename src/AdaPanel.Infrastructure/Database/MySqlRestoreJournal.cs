using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// A durable intent written before stdin is sent. DDL and nontransactional tables cannot be
/// rolled back as one import. A crash, timeout or failed SQL therefore requires human recovery,
/// never an automatic replay of a dump's DROP TABLE / INSERT statements.
/// Callers hold the database lifecycle lease throughout validation, execution and completion.
/// </summary>
internal sealed class MySqlRestoreJournal(JsonFileStore store)
{
    internal const string StatePath = "config/mysql-restore-attempts.json";

    internal sealed class Attempt
    {
        public string Id { get; set; } = string.Empty;
        public string Engine { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public DateTime StartedAtUtc { get; set; }
    }

    internal async Task EnsureCanStartAsync(string engine, string database, CancellationToken ct)
    {
        var attempts = await store.ReadAsync(StatePath, Array.Empty<Attempt>(), ct);
        ThrowIfUncertain(attempts, engine, database);
    }

    internal async Task<string> BeginAsync(string engine, string database, CancellationToken ct)
    {
        var attempt = new Attempt
        {
            Id = Guid.NewGuid().ToString("N"), Engine = engine, DatabaseName = database,
            StartedAtUtc = DateTime.UtcNow
        };
        await store.UpdateAsync(StatePath, Array.Empty<Attempt>(), attempts =>
        {
            ThrowIfUncertain(attempts, engine, database);
            return [.. attempts, attempt];
        }, ct);
        return attempt.Id;
    }

    internal Task CompleteAsync(string id, CancellationToken ct) =>
        store.UpdateAsync(StatePath, Array.Empty<Attempt>(), attempts => attempts.Where(x => x.Id != id).ToArray(), ct);

    /// <summary>Only after CREATE DATABASE actually succeeds: existing data is never deleted here.</summary>
    internal Task ClearForNewDatabaseAsync(string engine, string database, CancellationToken ct) =>
        store.UpdateAsync(StatePath, Array.Empty<Attempt>(), attempts => attempts.Where(x =>
            !DatabaseResourceIdentity.Same(x.Engine, x.DatabaseName, engine, database)).ToArray(), ct);

    private static void ThrowIfUncertain(IEnumerable<Attempt> attempts, string engine, string database)
    {
        var previous = attempts.FirstOrDefault(x =>
            DatabaseResourceIdentity.NormalizeEngine(x.Engine) == DatabaseResourceIdentity.NormalizeEngine(engine) &&
            (x.DatabaseName.Length == 0 || database.Length == 0 ||
             DatabaseResourceIdentity.Same(x.Engine, x.DatabaseName, engine, database)));
        if (previous is null) return;
        throw new AppException("database.restore_partial_retry_blocked", 409, 409,
            $"{previous.Engine}/{previous.DatabaseName}; {previous.StartedAtUtc:O}. " +
            "An earlier import failed or its completion is unconfirmed. No SQL was replayed. " +
            "Keep the imported tables/data and apply only the failed object manually. " +
            "An arbitrary SQL dump locks full imports for this engine until its uncertain recovery state is reviewed. " +
            "Changing DEFINER does not make replaying DROP TABLE or INSERT safe.");
    }
}
