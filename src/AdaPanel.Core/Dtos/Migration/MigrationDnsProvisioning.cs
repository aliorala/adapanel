namespace AdaPanel.Core.Dtos.Migration;

public sealed class MigrationDnsRecord
{
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public int Ttl { get; init; }
    public int Priority { get; init; }
}
public sealed class MigrationDnsSnapshot
{
    public bool Measured { get; init; }
    public bool Available { get; init; }
    public bool Exists { get; init; }
    public bool ConfigurationExists { get; init; }
    public string ZoneType { get; init; } = "";
    public string ZoneFile { get; init; } = "";
    public IReadOnlyList<MigrationDnsRecord> Records { get; init; } = [];
}
public sealed class MigrationDnsImportRequest
{
    public string SiteName { get; init; } = "";
    public long SiteId { get; init; }
    public string ApplicationRootPath { get; init; } = "";
    public string ZoneName { get; init; } = "";
    public IReadOnlyList<MigrationDnsRecord> Records { get; init; } = [];
}
public sealed class MigrationDnsImportResult
{
    public bool Attempted { get; set; }
    public bool ZoneCreated { get; set; }
    public bool Verified { get; set; }
    public bool PartialStatePossible { get; set; }
    public string ErrorCode { get; set; } = "";
    public List<string> CreatedResources { get; } = [];
    public List<string> Evidence { get; } = [];
}
