using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Chooses whole restore points for pruning. An explicit age limit expires an entire
/// old point; count-only pruning requires a usable replacement. Chains stay intact.
/// </summary>
internal static class BackupPrunePlanner
{
    internal sealed record RetentionProtection(BackupRecordDto Record, string Reason);
    internal sealed record PruneAnalysis(
        IReadOnlyList<BackupRecordDto> Selected,
        IReadOnlyList<RetentionProtection> RetainedExpired);

    private sealed record RestorePoint(
        string SourceKey,
        DateTime NewestUtc,
        List<BackupRecordDto> Records,
        bool Usable,
        bool HasAvailableRecord);

    /// <param name="records">Records already narrowed to the caller's scope.</param>
    /// <param name="cutoffUtc">Entire points older than this expire; MinValue disables age pruning.</param>
    /// <param name="keepCount">Minimum newest points for count pruning; never overrides explicit age expiry.</param>
    /// <param name="kullanilabilir">Whether the record has a usable local or remote archive.</param>
    /// <param name="canReplace">Optional content/coverage policy, given the old point and its retained replacement.</param>
    public static IReadOnlyList<BackupRecordDto> Select(
        IReadOnlyCollection<BackupRecordDto> records,
        DateTime cutoffUtc,
        int keepCount,
        Func<BackupRecordDto, bool> kullanilabilir,
        Func<IReadOnlyCollection<BackupRecordDto>, IReadOnlyCollection<BackupRecordDto>, bool>? canReplace = null)
        => Analyze(records, cutoffUtc, keepCount, kullanilabilir, canReplace).Selected;

    public static PruneAnalysis Analyze(
        IReadOnlyCollection<BackupRecordDto> records,
        DateTime cutoffUtc,
        int keepCount,
        Func<BackupRecordDto, bool> kullanilabilir,
        Func<IReadOnlyCollection<BackupRecordDto>, IReadOnlyCollection<BackupRecordDto>, bool>? canReplace = null)
    {
        var points = records
            // Chain IDs are not authority to combine different sources. Canonical source
            // keys preserve quoted PostgreSQL case while legacy engine aliases still match.
            .GroupBy(record => (Source: SourceKey(record), Point: PointKey(record)))
            .Select(group =>
            {
                var members = group.ToList();
                var availability = members.Select(kullanilabilir).ToArray();
                var usable = availability.All(available => available) && IsStructurallyComplete(members);
                return new RestorePoint(group.Key.Source, members.Max(record => record.CreatedAtUtc),
                    members, usable, availability.Any(available => available));
            })
            .ToArray();

        var selected = new List<BackupRecordDto>();
        foreach (var source in points.GroupBy(point => point.SourceKey, StringComparer.Ordinal))
        {
            var retained = new List<RestorePoint>();
            var position = 0;
            foreach (var point in source.OrderByDescending(point => point.NewestUtc))
            {
                var countProtected = keepCount > 0 && position < keepCount;
                position++;
                var countExceeded = keepCount > 0 && !countProtected;
                // Age is the user's explicit upper bound, including legacy/last copies.
                // NewestUtc is measured across the whole point, so an active incremental
                // link keeps its old base; fully expired chains are selected as a unit.
                if (point.NewestUtc < cutoffUtc)
                {
                    selected.AddRange(point.Records);
                    continue;
                }
                if (countProtected || !countExceeded)
                {
                    retained.Add(point);
                    continue;
                }

                var replaced = retained.Any(replacement => replacement.Usable &&
                    replacement.NewestUtc > point.NewestUtc &&
                    (canReplace?.Invoke(point.Records, replacement.Records) ?? true));

                // When nothing in an old point can be used, count pruning can discard its
                // stale metadata after retaining N newer points. A partly usable chain
                // still requires a replacement so its recoverable prefix is not lost.
                if (replaced || !point.HasAvailableRecord)
                    selected.AddRange(point.Records);
                else
                    retained.Add(point);
            }
        }

        return new PruneAnalysis(selected, []);
    }

    public static string SourceKey(BackupRecordDto record) => BackupSourceIdentity.Key(record);

    private static string PointKey(BackupRecordDto record) =>
        (string.IsNullOrWhiteSpace(record.ChainId) ? $"id:{record.Id}" : $"chain:{record.ChainId}").ToUpperInvariant();

    private static bool IsStructurallyComplete(List<BackupRecordDto> records)
    {
        if (string.IsNullOrWhiteSpace(records[0].ChainId)) return true;
        if (records.Any(record => record.ChainIndex < 0) ||
            records.Select(record => record.ChainIndex).Distinct().Count() != records.Count) return false;
        var latest = records.MaxBy(record => record.ChainIndex)!;
        return !BackupChainPlanner.Resolve(records, latest.Id, _ => true).Broken;
    }
}
