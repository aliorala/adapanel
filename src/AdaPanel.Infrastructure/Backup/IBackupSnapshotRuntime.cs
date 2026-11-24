namespace AdaPanel.Infrastructure.Backup;

/// <summary>A single, read-only filesystem instant; disposal releases only this capture.</summary>
public interface IBackupSnapshotRuntime
{
    Task<IBackupSnapshotLease> CaptureAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken);
}

public interface IBackupSnapshotLease : IAsyncDisposable
{
    DateTime CapturedAtUtc { get; }
    string ResolveRoot(string originalRoot);
}
