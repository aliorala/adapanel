using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// An archive can be partly restorable without being a safe replacement for an older
/// recovery point. This policy concerns retention, not permission to restore its files.
/// The caller separately checks copy availability and the complete chain structure.
/// </summary>
internal static class BackupReplacementPolicy
{
    public static bool CanReplace(
        IReadOnlyCollection<BackupRecordDto> previous,
        IReadOnlyCollection<BackupRecordDto> replacement)
    {
        if (previous.Count == 0 || replacement.Count == 0 ||
            !previous.All(HasValidDatabaseReferences) || !replacement.All(HasCompleteContent) ||
            !ComponentsCovered(previous, replacement))
            return false;

        // Removing this record also deletes its off-site copy. A local-only successor or
        // a remote delta without its remote base cannot replace that disaster recovery point.
        if (previous.Any(HasPossibleRemoteCopy) && !replacement.All(HasConfirmedRemoteCopy))
            return false;

        // Older records did not measure database inclusion. Keep their established
        // legacy-to-legacy rotation, but never reinterpret unknown as a measured empty set.
        if (previous.Any(record => record.IncludedDatabases is null) ||
            replacement.Any(record => record.IncludedDatabases is null))
            return previous.All(record => record.IncludedDatabases is null) &&
                replacement.All(record => record.IncludedDatabases is null);

        var required = DatabaseKeys(previous);
        // Every link contains full database dumps. A dump in the old base says nothing
        // about a database intentionally omitted from the latest link's snapshot.
        var included = DatabaseKeys([replacement.MaxBy(record => record.ChainIndex)!]);
        return required.IsSubsetOf(included);
    }

    private static bool ComponentsCovered(
        IReadOnlyCollection<BackupRecordDto> previous,
        IReadOnlyCollection<BackupRecordDto> replacement)
    {
        if (previous.Concat(replacement).Any(record => record.IncludedComponents?.Any(component =>
                !BackupComponentCoverage.IsValid(component)) == true)) return false;

        if (previous.Any(record => record.IncludedComponents is null) ||
            replacement.Any(record => record.IncludedComponents is null))
            return previous.All(record => record.IncludedComponents is null) &&
                replacement.All(record => record.IncludedComponents is null);

        var required = previous.SelectMany(record => record.IncludedComponents!)
            .Select(BackupComponentCoverage.Key).ToHashSet(StringComparer.Ordinal);
        var included = replacement.MaxBy(record => record.ChainIndex)!.IncludedComponents!
            .Select(BackupComponentCoverage.Key).ToHashSet(StringComparer.Ordinal);
        return required.IsSubsetOf(included);
    }

    private static bool HasPossibleRemoteCopy(BackupRecordDto record) =>
        !string.IsNullOrWhiteSpace(record.RemoteUri) &&
        record.RemoteState != BackupPresence.Missing;

    private static bool HasConfirmedRemoteCopy(BackupRecordDto record) =>
        HasPossibleRemoteCopy(record) && record.RemoteState != BackupPresence.None &&
        (record.RemoteState == BackupPresence.Present ||
         (!record.RemoteMissing && record.Status == "created_remote_synced"));

    public static bool HasCompleteContent(BackupRecordDto record)
    {
        // A remote upload failure can leave a complete local archive. Content failures
        // and restore statuses do not provide the same evidence: restoring resets warnings.
        if (record.Status is not ("created" or "created_remote_synced" or "created_remote_failed"))
            return false;
        if (record.IntegrityState != BackupIntegrity.Sound &&
            !(record.IntegrityState == BackupIntegrity.Unknown && record.IncludedDatabases is null))
            return false;
        if ((record.Warnings ?? []).Any(warning => warning is null || warning.MessageKey is not
            ("backup.remote_unencrypted" or "backup.compression_failed" or "backup.remote_upload_failed")))
            return false;
        return HasValidDatabaseReferences(record);
    }

    private static bool HasValidDatabaseReferences(BackupRecordDto record) =>
        record.IncludedDatabases is null || record.IncludedDatabases.All(database => database is not null &&
            !string.IsNullOrWhiteSpace(database.Engine) && !string.IsNullOrWhiteSpace(database.DatabaseName));

    private static HashSet<string> DatabaseKeys(IEnumerable<BackupRecordDto> records) => records
        .SelectMany(record => record.IncludedDatabases ?? [])
        .Where(database => database is not null)
        .Select(database => DatabaseResourceIdentity.Key(database.Engine, database.DatabaseName))
        .ToHashSet(StringComparer.Ordinal);
}
