namespace AdaPanel.Core.Dtos.Migration;

/// <summary>Sensitive connection request/protected job state only. Never log this object.</summary>
public sealed class AdaPanelSourceConnection
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiToken { get; init; } = string.Empty;
    public string AcceptedCertificateFingerprint { get; init; } = string.Empty;
}

public static class AdaPanelMigrationSourceProtocol
{
    public const string VersionHeader = "X-AdaPanel-Migration-Source-Version";
    public const string Version = "1";
    public const string InventoryPath = "/api/v1/migration/source/inventory";
    public const string ExportPath = "/api/v1/migration/source/export";
}
