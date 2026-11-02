using System.Net;
using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

public sealed partial class BackupService
{
    // Only server-wide retention owns the whole day tree. Subscription retention must
    // never sweep a shared day containing another customer's unindexed files.
    private async Task PruneExpiredDayFoldersAsync(int retentionDays, BackupPruneRemoteOptions? remote,
        BackupPruneResultDto result, Func<CancellationToken, Task>? beforeMutation, CancellationToken ct)
    {
        var expiredThrough = BackupRetentionCalendar.GetExpiredThroughLocalDate(DateTime.UtcNow, retentionDays);
        if (expiredThrough is null) return;
        if (beforeMutation is not null) await beforeMutation(ct);
        var remaining = await GetBackupsAsync(ct);
        // Read the same persisted setting/default without invoking unrelated preference
        // resolution. An archive's own path is never authority to choose a deletion root.
        var backupRoot = await Services.PanelPreferencesService.ReadBackupRootAsync(fileStore, ct);
        var local = await BackupExpiredLocalDayCleanup.SweepAsync(backupRoot,
            expiredThrough.Value, remaining.Select(record => record.Path ?? string.Empty).ToArray(), beforeMutation, ct);
        result.LocalDeleteFailed += local.Failures.Count;
        result.Failures.AddRange(local.Failures);
        if (local.RemovedDays > 0)
            result.RetentionNotes.Add($"local_expired_day_folders_removed={local.RemovedDays}");

        // A directory boundary is mandatory; historical per-file URIs are not permission
        // to infer a parent and recursively delete it. Other stores keep record-based pruning.
        if (remote is null || !Uri.TryCreate(remote.RemoteUrl, UriKind.Absolute, out var root)
            || root.Scheme != "ftp") return;
        await EnsureRemoteTargetAllowedAsync(new BackupTarget("ftp", root, "", "", ""), ct);
        var protectedUris = remaining.Select(record =>
                Uri.TryCreate(record.RemoteUri, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>().ToArray();
        var ftp = await BackupExpiredFtpDayCleanup.SweepAsync(FtpCommands, root,
            new NetworkCredential(remote.UserName, remote.Password), expiredThrough.Value, protectedUris, beforeMutation, ct);
        result.RemoteDeleteFailed += ftp.Failures.Count;
        result.Failures.AddRange(ftp.Failures);
        if (ftp.RemovedDays > 0)
            result.RetentionNotes.Add($"ftp_expired_day_folders_removed={ftp.RemovedDays}");
    }
}
