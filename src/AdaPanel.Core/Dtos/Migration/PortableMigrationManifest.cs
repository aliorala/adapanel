using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaPanel.Core.Dtos.Migration;

[JsonConverter(typeof(PortableMigrationProviderJsonConverter))]
public enum PortableMigrationProvider { Adapanel, Cpanel, Plesk }

public sealed class PortableMigrationManifest
{
    public int Version { get; init; } = 1;
    public Guid PackageId { get; init; }
    public PortableMigrationProvider Provider { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string SourceName { get; init; } = "";
    public bool Complete { get; init; }
    public List<PortableMigrationIssue> Issues { get; init; } = [];
    public List<PortableMigrationSite> Sites { get; init; } = [];
    public List<PortableMigrationDatabase> Databases { get; init; } = [];
    public List<PortableMigrationMailDomain> MailDomains { get; init; } = [];
    public List<PortableMigrationDnsZone> DnsZones { get; init; } = [];
    public List<PortableMigrationTask> Tasks { get; init; } = [];
    public List<PortableMigrationPayload> Payloads { get; init; } = [];
}
public sealed class PortableMigrationIssue
{
    public string Code { get; init; } = "";
    public string ResourceId { get; init; } = "";
    public string Detail { get; init; } = "";
}
public sealed class PortableMigrationSite
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string SourcePhysicalPath { get; init; } = "";
    public string ContentPath { get; init; } = "";
    public string RuntimeProfile { get; init; } = "";
    public string DocumentRootRelativePath { get; init; } = "";
    public string PhpVersion { get; init; } = "";
    public long? FileCount { get; init; }
    public List<string> EmptyDirectories { get; init; } = [];
    public List<string> HostNames { get; init; } = [];
}
public sealed class PortableMigrationDatabase
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Engine { get; init; } = "";
    public List<string> SiteIds { get; init; } = [];
    public string DumpPath { get; init; } = "";
    public string Format { get; init; } = "";
    public string? Charset { get; init; }
    public string? Collation { get; init; }
}
public sealed class PortableMigrationMailDomain
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<string> SiteIds { get; init; } = [];
    public bool? Active { get; init; }
    public List<PortableMigrationMailList> Lists { get; init; } = [];
    public List<PortableMigrationMailAccount> Accounts { get; init; } = [];
    public List<PortableMigrationMailAlias> Aliases { get; init; } = [];
}
public sealed class PortableMigrationMailAccount
{
    public string Email { get; init; } = "";
    public long? EmailCount { get; init; }
    public bool? Active { get; init; }
    public long QuotaMb { get; init; }
    public string ArchivePath { get; init; } = "";
    public string ArchiveFormat { get; init; } = "";
}
public sealed class PortableMigrationMailAlias
{
    public string Address { get; init; } = "";
    public string Destination { get; init; } = "";
    public bool? Active { get; init; }
}
public sealed class PortableMigrationDnsZone
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<string> SiteIds { get; init; } = [];
    public List<PortableMigrationDnsRecord> Records { get; init; } = [];
}
public sealed class PortableMigrationDnsRecord
{
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public int Ttl { get; init; }
    public int Priority { get; init; }
}
public sealed class PortableMigrationTask
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<string> SiteIds { get; init; } = [];
    public string Kind { get; init; } = "";
    public string Command { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string ScheduleType { get; init; } = "";
    public string ScheduleValue { get; init; } = "";
    public string SourcePlatform { get; init; } = "";
    public int Interval { get; init; } = 1;
    public string TimeOfDay { get; init; } = "";
    public string RunAs { get; init; } = "";
    public int TimeLimitHours { get; init; }
    public string Url { get; init; } = "";
    public string PhpVersion { get; init; } = "";
    public bool ScheduleUnknown { get; init; }
    public List<string> ArtifactPaths { get; init; } = [];
}
public sealed class PortableMigrationPayload
{
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
}
public sealed record PortableMigrationPackage(Guid Id, PortableMigrationManifest Manifest);

public sealed class PortableMigrationProviderJsonConverter : JsonConverter<PortableMigrationProvider>
{
    public override PortableMigrationProvider Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "adapanel" => PortableMigrationProvider.Adapanel,
            "cpanel" => PortableMigrationProvider.Cpanel,
            "plesk" => PortableMigrationProvider.Plesk,
            _ => throw new JsonException("migration.package.provider_invalid")
        } : throw new JsonException("migration.package.provider_invalid");
    public override void Write(Utf8JsonWriter writer, PortableMigrationProvider value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            PortableMigrationProvider.Adapanel => "adapanel",
            PortableMigrationProvider.Cpanel => "cpanel",
            PortableMigrationProvider.Plesk => "plesk",
            _ => throw new JsonException("migration.package.provider_invalid")
        });
}
public sealed class PortableMigrationMailList
{
    public string Address { get; init; } = "";
    public string Description { get; init; } = "";
    public List<string> Recipients { get; init; } = [];
    public List<string> Aliases { get; init; } = [];
    public string PostingPolicy { get; init; } = "";
    public List<string> AllowedSenders { get; init; } = [];
}