using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.SiteBuilder;

namespace AdaPanel.Infrastructure.Backup;

public sealed partial class BackupService
{
    private static readonly SemaphoreSlim RemoteRetryGate = new(1, 1);

    // Only failed first uploads: completed remote uploads cannot be replayed here.
    // The administrator confirms the CURRENT configured destination, which may have changed.
    public Task<BackupRecordDto> RetryRemoteUploadAsync(string backupId, string expectedRemoteUrl,
        CancellationToken cancellationToken = default) => RetryRemoteUploadCoreAsync(backupId, expectedRemoteUrl, null, cancellationToken);

    public Task<BackupRecordDto> RetryRemoteUploadAsync(string backupId, string expectedRemoteUrl,
        Func<BackupRecordDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) =>
        RetryRemoteUploadCoreAsync(backupId, expectedRemoteUrl, beforeMutation, cancellationToken);

    private async Task<BackupRecordDto> RetryRemoteUploadCoreAsync(string backupId, string expectedRemoteUrl,
        Func<BackupRecordDto, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        if (!await RemoteRetryGate.WaitAsync(0, cancellationToken))
            throw new AppException("runningjob.backup_in_progress", 409, 409);
        try
        {
            var snapshot = await EnterRecordAsync(backupId, cancellationToken, beforeMutation);
            using var sourceLease = snapshot.Lease;
            var record = snapshot.Record;
            if (record.Status != "created_remote_failed" || record.TargetType != "ftp"
                || !string.IsNullOrEmpty(record.RemoteUri)
                || string.Equals(record.Type, "subscription", StringComparison.OrdinalIgnoreCase))
                throw new AppException("backup.retry_not_available", 409, 409);

            var (targetType, remoteUrl, credentials) = await RemoteCredentials.ReadUploadTargetAsync(cancellationToken);
            if (targetType != "ftp" || string.IsNullOrWhiteSpace(remoteUrl)
                || !string.Equals(expectedRemoteUrl, remoteUrl, StringComparison.Ordinal))
                throw new AppException("backup.retry_target_changed", 409, 409);
            var target = ResolveTarget(targetType, remoteUrl, credentials.UserName, credentials.Password, credentials.Region);
            if (target.Uri is null || IsPinnedArchiveTarget(Uri.UnescapeDataString(target.Uri.AbsolutePath).TrimEnd('/').Split('/').Last()))
                throw new AppException("backup.retry_not_available", 409, 409);
            await EnsureRemoteTargetAllowedAsync(target, cancellationToken);

            var preferences = await preferencesService.GetAsync(cancellationToken);
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(preferences.DefaultBackupRoot));
            if (string.IsNullOrWhiteSpace(record.Path) || !File.Exists(record.Path))
                throw new AppException("backup.file_missing", 410, 410);
            var path = Path.GetFullPath(record.Path);
            var relative = Path.GetRelativePath(root, path);
            var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(relative) || parts.Length < 3 || parts.Any(p => p is "." or "..")
                || !DateTime.TryParseExact(parts[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _)
                || !string.Equals(parts[^1], record.Name, StringComparison.Ordinal))
                throw new AppException("backup.retry_not_available", 409, 409);
            SiteBuilderPathGuard.EnsureNoReparsePointOnPath(root, path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            // Hold the original file against writes/deletion while checking and transmitting it.
            // Delete/prune retain their record if this lease prevents local deletion.
            await using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (string.IsNullOrWhiteSpace(record.ContentHash))
                throw new AppException("backup.retry_not_available", 409, 409);
            var integrity = await BackupContentHash.VerifyAsync(record.ContentHash, path, cancellationToken);
            if (integrity is BackupIntegrityCheck.NotStamped or BackupIntegrityCheck.Unmeasurable)
                throw new AppException("backup.retry_not_available", 409, 409);
            if (lease.Length == 0 || lease.Length != record.Size || integrity == BackupIntegrityCheck.Mismatch)
                throw new AppException("backup.integrity_mismatch", 409, 409);

            var placement = new BackupPlacement(parts[0], parts[1..^1], Path.GetDirectoryName(path)!);
            var remoteUri = await UploadToRemoteAsync(path, record.Name, target, placement, cancellationToken);
            // Transfer succeeded: commit that fact even if the browser disconnected at this point.
            await fileStore.UpdateAsync(IndexPath, Array.Empty<BackupRecordDto>(), existing =>
            {
                var current = existing.FirstOrDefault(x => x.Id == backupId)
                    ?? throw new AppException("backup.not_found", 404, 404);
                current.RemoteUri = remoteUri;
                current.Status = current.Warnings.Any(w => w.MessageKey is not ("backup.remote_upload_failed"
                    or "backup.remote_unencrypted" or "backup.compression_failed"))
                    ? "created_with_errors" : "created_remote_synced";
                current.RemoteMissing = false;
                current.RemoteState = BackupPresence.Present;
                current.RemoteVerifiedUtc = DateTime.UtcNow;
                current.Warnings.RemoveAll(w => w.MessageKey == "backup.remote_upload_failed");
                return existing;
            }, CancellationToken.None);
            return (await GetBackupsAsync(CancellationToken.None)).Single(x => x.Id == backupId);
        }
        finally { RemoteRetryGate.Release(); }
    }
}
