namespace AdaPanel.Core.Dtos.Databases;

/// <summary>Bir veritabanının hangi siteye ait olduğunun kaydı (Plesk: abonelikteki veritabanları).
/// Site silinince bağlı veritabanları da cascade ile gider.</summary>
public sealed class SiteDatabaseLinkDto
{
    /// <summary>Motor (mariadb | mysql | postgresql | mssql) — DeleteDatabaseAsync'in kabul ettiği biçim.</summary>
    public string Engine { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = string.Empty;

    public string SiteName { get; set; } = string.Empty;
}
