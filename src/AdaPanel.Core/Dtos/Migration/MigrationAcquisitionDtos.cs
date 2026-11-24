using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Migration;

public sealed class MigrationConnectionRequest
{
    [RegularExpression("^(adapanel|cpanel|plesk)$")] public string Provider { get; set; } = "adapanel";
    public AdaPanelSourceConnection? AdaPanel { get; set; }
    public LiveMigrationConnection? Ssh { get; set; }
}

public sealed class MigrationAcquireRequest
{
    public Guid ClientRequestId { get; set; }
    public MigrationConnectionRequest Connection { get; set; } = new();
    [MaxLength(1000)] public string[] SourceUnitIds { get; set; } = [];
    public MigrationExportRequest AdaPanelSelection { get; set; } = new();
}

public sealed class MigrationDiscoveryDto
{
    public string Provider { get; set; } = "";
    public MigrationSourceInventoryDto? AdaPanel { get; set; }
    public LiveMigrationDiscovery? HostingPanel { get; set; }
}

public sealed class MigrationAcquisitionDto
{
    public Guid ClientRequestId { get; set; }
    public long? DownloadedBytes { get; set; }
    public long NormalizedPayloadBytes { get; set; }
    public List<MigrationAcquisitionRecoveryDto> RemoteRecoveries { get; set; } = [];
    public Guid Id { get; set; }
    public string Provider { get; set; } = "";
    public string SourceHost { get; set; } = "";
    public string[] SourceUnitIds { get; set; } = [];
    public string State { get; set; } = "pending";
    public string Stage { get; set; } = "pending";
    public string ErrorCode { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public bool CancellationRequested { get; set; }
    public List<Guid> PackageIds { get; set; } = [];
    public List<MigrationAcquisitionPackageRecoveryDto> PackageRecoveries { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool RemoteCleanupComplete { get; set; }
    public string RemoteRecoveryWorkspace { get; set; } = "";
    public List<string> RemoteRecoveryFiles { get; set; } = [];
    public bool ContentReady => State == "completed" && PackageIds.Count > 0;
}

public sealed class MigrationAcquisitionRecoveryDto
{
    public string SourceUnitId { get; set; } = "";
    public string Workspace { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public bool ProcessCompletionConfirmed { get; set; }
    public bool CleanupComplete { get; set; }
}

public sealed class MigrationAcquisitionPackageRecoveryDto
{
    public Guid PackageId { get; set; }
    public string Provider { get; set; } = "";
    public string SourceUnitId { get; set; } = "";
    public bool Verified { get; set; }
    public string RequiredAction { get; set; } = "migration.acquisition_review_unregistered_package";
}
