namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// Sunucu taraflı veritabanı taşıma (Plesk gibi): kaynak (uzak olabilir) MySQL/MariaDB'den
/// mysqldump ile alıp yerel hedef veritabanına aktarır. Tarayıcı upload'ı yok → 413/timeout yaşanmaz.
/// </summary>
public sealed class DatabaseTransferRequest
{
    /// <summary>Hedef motor: "mariadb" veya "mysql" (yerel).</summary>
    public string Engine { get; set; } = "mariadb";

    public string SourceHost { get; set; } = string.Empty;

    public int SourcePort { get; set; } = 3306;

    public string SourceUserName { get; set; } = string.Empty;

    public string SourcePassword { get; set; } = string.Empty;

    public string SourceDatabase { get; set; } = string.Empty;

    /// <summary>Boş bırakılırsa kaynak veritabanı adı kullanılır.</summary>
    public string TargetDatabase { get; set; } = string.Empty;
}
