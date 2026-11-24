namespace AdaPanel.Core.Dtos.Databases;

public sealed class MigrationDatabasePlan
{
    public bool Supported { get; init; }
    public IReadOnlyList<string> Blockers { get; init; } = [];
    public bool CanImport => Supported && Blockers.Count == 0;
}

public sealed class MigrationDatabaseImportResult
{
    public bool Imported { get; init; }
    public bool Verified { get; init; }
    public bool Cancelled { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    /// <summary>A server may commit DDL even if its acknowledgement is lost or cancellation occurs.</summary>
    public bool PartialStatePossible { get; init; }
    public IReadOnlyList<string> AttemptedResources { get; init; } = [];
    /// <summary>Partial resources are deliberately retained and must be reviewed; never automatically replay the dump.</summary>
    public IReadOnlyList<string> CreatedResources { get; init; } = [];
    public IReadOnlyList<string> ObjectInventory { get; init; } = [];
    /// <summary>Dedicated sensitive payload; never include this in generic job details or logs.</summary>
    public MigrationDatabaseCredentials? Credentials { get; init; }
}

public sealed class MigrationDatabaseCredentials
{
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Host { get; init; } = "localhost";
    public string Database { get; init; } = string.Empty;
}
