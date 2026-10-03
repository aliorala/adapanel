namespace AdaPanel.Infrastructure.Backup;

public sealed partial class BackupService
{
    // A timestamp is a display hint, not an artifact identity: sequential requests and
    // independent job slots can create backups of the same source within one second.
    // Callers pass a sanitized layout label (or a fixed prefix), never a raw source path.
    private string NewArtifactFileName(string label, string extension, bool incremental = false) =>
        $"{label}-{(timeProvider ?? TimeProvider.System).GetUtcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}{(incremental ? "-inc" : string.Empty)}.{extension}";
}
