using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Migration;

public sealed class MigrationSourceInventoryDto
{
    public string Provider { get; init; } = "adapanel";
    public DateTimeOffset MeasuredAtUtc { get; init; }
    public bool Complete { get; init; }
    public List<PortableMigrationSite> Sites { get; init; } = [];
    public List<PortableMigrationDatabase> Databases { get; init; } = [];
    public List<PortableMigrationTask> Tasks { get; init; } = [];
    public List<PortableMigrationIssue> Issues { get; init; } = [];
}

public sealed class MigrationExportRequest
{
    public bool AllSites { get; set; }
    [MaxLength(10000)] public string[] SiteIds { get; set; } = [];
    public bool AllDatabases { get; set; }
    [MaxLength(10000)] public string[] DatabaseIds { get; set; } = [];
    public bool IncludeMail { get; set; } = true;
    public bool IncludeDns { get; set; } = true;
    public bool IncludeTasks { get; set; }
    [MaxLength(10000)] public string[] TaskIds { get; set; } = [];
}

// Server-local artifact; controllers return a FileStream, never this path as JSON.
public sealed record MigrationExportArtifact(string FilePath, string FileName, string Workspace);
