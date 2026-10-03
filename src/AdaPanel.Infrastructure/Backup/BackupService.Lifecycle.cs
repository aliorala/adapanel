using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

public sealed partial class BackupService
{
    private Task<IDisposable> EnterSourceAsync(string type, string source, CancellationToken cancellationToken) =>
        BackupSourceLifecycle.EnterAsync(fileStore.ResolvePath(IndexPath), type, source, cancellationToken);

    private async Task<(IDisposable Lease, BackupRecordDto Record, IReadOnlyCollection<BackupRecordDto> Records)>
        EnterRecordAsync(string backupId, CancellationToken cancellationToken,
            Func<BackupRecordDto, CancellationToken, Task>? beforeMutation = null)
    {
        var before = (await GetBackupsAsync(cancellationToken)).Where(item => item.Id == backupId).ToArray();
        if (before.Length == 0) throw new AppException("backup.not_found", 404, 404);
        if (before.Length != 1) throw new AppException("backup.chain_ambiguous", 409, 409);
        var lease = await EnterSourceAsync(before[0].Type, before[0].Source, cancellationToken);
        try
        {
            var records = await GetBackupsAsync(cancellationToken);
            var current = records.Where(item => item.Id == backupId).ToArray();
            if (current.Length == 0) throw new AppException("backup.not_found", 404, 404);
            if (current.Length != 1) throw new AppException("backup.chain_ambiguous", 409, 409);
            if (!BackupSourceIdentity.Same(before[0], current[0]))
                throw new AppException("backup.chain_changed", 409, 409);
            if (beforeMutation is not null)
                await beforeMutation(current[0], cancellationToken);
            return (lease, current[0], records);
        }
        catch { lease.Dispose(); throw; }
    }
}
