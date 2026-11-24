namespace AdaPanel.Core.Dtos.Migration;

public sealed class LiveMigrationSourceUnit
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
}

public sealed class LiveMigrationDiscovery
{
    public bool Supported { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    public string HostFingerprint { get; init; } = string.Empty;
    public string SourceVersion { get; init; } = string.Empty;
    public IReadOnlyList<LiveMigrationSourceUnit> Units { get; init; } = [];
}

public sealed class LiveSourceArtifact
{
    public string ServerGeneratedPath { get; init; } = string.Empty;
    public string SourceUnitId { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string SourceVersion { get; init; } = string.Empty;
    public bool Full { get; init; }
    public bool RequiresContentValidation => true;
    public long Bytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

public sealed class LiveMigrationAcquireResult
{
    public IReadOnlyList<LiveSourceArtifact> Artifacts { get; init; } = [];
    public string ErrorCode { get; init; } = string.Empty;
    public string HostFingerprint { get; init; } = string.Empty;
    public bool Cancelled { get; init; }
    public bool ProcessCompletionConfirmed { get; init; } = true;
    public bool RemoteCleanupComplete { get; init; }
    /// <summary>Persist with the job for recovery. Only server-generated owned paths are listed.</summary>
    public string RemoteWorkspace { get; init; } = string.Empty;
    public string RecoveryManifestPath { get; init; } = string.Empty;
    public IReadOnlyList<string> AttemptedRemoteFiles { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = ["migration.live_snapshot_not_atomic"];
}

public sealed class LiveMigrationProgress
{
    public string Stage { get; init; } = string.Empty;
    public string SourceUnitId { get; init; } = string.Empty;
    public long Bytes { get; init; }
}
