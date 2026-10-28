using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Diagnostics;

public sealed class InstallationReportingDto
{
    public bool Enabled { get; init; }
    // Legacy response fields stay empty; the client no longer collects location or provider fields.
    public string Location { get; init; } = "";
    public string Datacenter { get; init; } = "";
    public DateTimeOffset? ConsentAtUtc { get; init; }
    public DateTimeOffset? LastAttemptAtUtc { get; init; }
    public DateTimeOffset? LastAcceptedAtUtc { get; init; }
    public DateTimeOffset? NextAttemptAtUtc { get; init; }
    public string LastError { get; init; } = "";
    public string Revision { get; init; } = "";
}

public sealed class UpdateInstallationReportingRequest
{
    public bool Enabled { get; init; }
    // Accepted only for old clients; ignored and never persisted or transmitted.
    public string Location { get; init; } = "";
    public string Datacenter { get; init; } = "";
    [Required] public string Revision { get; init; } = "";
}
