using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Diagnostics;

public sealed class InstallationReportingDto
{
    public bool Enabled { get; init; }
    public string Location { get; init; } = "";
    public string Datacenter { get; init; } = "";
    public DateTimeOffset? ConsentAtUtc { get; init; }
    public DateTimeOffset? LastAttemptAtUtc { get; init; }
    public DateTimeOffset? LastAcceptedAtUtc { get; init; }
    public string LastError { get; init; } = "";
    public string Revision { get; init; } = "";
}

public sealed class UpdateInstallationReportingRequest
{
    public bool Enabled { get; init; }
    [StringLength(100)] public string Location { get; init; } = "";
    [StringLength(100)] public string Datacenter { get; init; } = "";
    [Required] public string Revision { get; init; } = "";
}
