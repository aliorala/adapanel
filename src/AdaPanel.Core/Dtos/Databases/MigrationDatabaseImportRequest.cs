namespace AdaPanel.Core.Dtos.Databases;

public sealed class MigrationDatabaseImportRequest
{
    public string Engine { get; init; } = string.Empty;
    public string TargetDatabase { get; init; } = string.Empty;
    /// <summary>Absolute local path already validated by the migration package boundary.</summary>
    public string DumpPath { get; init; } = string.Empty;
    public string TargetUserName { get; init; } = string.Empty;
    public string TargetPassword { get; init; } = string.Empty;
    public bool AdaptDefiners { get; init; } = true;
}
