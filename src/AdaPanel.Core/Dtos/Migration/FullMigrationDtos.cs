using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AdaPanel.Core.Dtos.Migration;

public sealed class MigrationPlanRequest
{
    [MinLength(1), MaxLength(1000)] public Guid[] PackageIds { get; set; } = [];
    public bool AllSites { get; set; }
    public List<string> SiteIds { get; set; } = [];
    public bool AllDatabases { get; set; }
    public List<string> DatabaseIds { get; set; } = [];
    public bool IncludeMail { get; set; } = true;
    public bool IncludeDns { get; set; } = true;
    public bool IncludeTasks { get; set; }
    public List<string> TaskIds { get; set; } = [];
    public string TargetBasePath { get; set; } = @"C:\inetpub\wwwroot";
    public List<MigrationTargetMapping> Mappings { get; set; } = [];
    public Dictionary<string, string> AddressMappings { get; set; } = [];
    public bool AcceptNewCredentials { get; set; }
}

public sealed class MigrationTargetMapping
{
    // Resource keys include the immutable package id; ids from different accounts cannot collide.
    public string ResourceId { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string RuntimeProfile { get; set; } = "auto";
    public string DatabaseUserName { get; set; } = "";
    public string LinkedSiteId { get; set; } = "";
    // Explicit fallback only when the source does not prove mailbox quota/login state.
    // null means undecided; zero is a deliberate unlimited quota.
    public long? UnknownMailboxQuotaMb { get; set; }
    public bool? UnknownMailboxActive { get; set; }
    public bool? UnknownMailDomainActive { get; set; }
    public bool? UnknownMailAliasActive { get; set; }
    public string TaskUrl { get; set; } = "";
    public string TaskCommand { get; set; } = "";
    public string TaskArguments { get; set; } = "";
    public string TaskWorkingDirectory { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string TaskScheduleType { get; set; } = "";
    public int TaskInterval { get; set; } = 1;
    public string TaskTimeOfDay { get; set; } = "00:00";
    public bool TaskMappingConfirmed { get; set; }
}

public sealed class MigrationPlanDto
{
    public Guid Id { get; set; }
    [JsonIgnore] public Guid OwnerId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string PlanHash { get; set; } = "";
    public MigrationPlanRequest Selection { get; set; } = new();
    public Dictionary<Guid, string> ManifestHashes { get; set; } = [];
    public List<MigrationPlanItemDto> Items { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
    public List<string> RequiredActions { get; set; } = [];
    public bool CanApply => Items.Count > 0 && Blockers.Count == 0 && Items.All(item => item.Blockers.Count == 0);
}

public sealed class MigrationPlanItemDto
{
    public string Id { get; set; } = "";
    public Guid PackageId { get; set; }
    public string Kind { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public MigrationTargetMapping Target { get; set; } = new();
    public List<string> Dependencies { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class MigrationOperationDto
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public string State { get; set; } = "pending";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public bool CancellationRequested { get; set; }
    public List<MigrationItemResultDto> Items { get; set; } = [];
    public List<string> RequiredActions { get; set; } = [];
    public bool CredentialsAvailable { get; set; }
    public bool DataTransferred => State is "completed" or "completed_with_actions";
}

public sealed class MigrationItemResultDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string State { get; set; } = "pending";
    public string ErrorCode { get; set; } = "";
    public bool PartialStatePossible { get; set; }
    public List<string> CreatedResources { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public List<string> RequiredActions { get; set; } = [];
}

public sealed class MigrationCreatedCredential
{
    public string ItemId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Host { get; set; } = "";
}

public sealed class MigrationComponentResult
{
    public bool Verified { get; set; }
    public bool PartialStatePossible { get; set; }
    public string ErrorCode { get; set; } = "";
    public List<string> CreatedResources { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public List<string> RequiredActions { get; set; } = [];
}

public sealed class MigrationTargetCheck
{
    public List<string> Blockers { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public static class MigrationResourceKey
{
    public static string Create(Guid packageId, string resourceId) => packageId.ToString("N") + ":" + resourceId;
}
