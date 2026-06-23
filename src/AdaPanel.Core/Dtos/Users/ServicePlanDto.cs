namespace AdaPanel.Core.Dtos.Users;

/// <summary>Plesk "Service Plan" karşılığı: müşterilere atanabilen yeniden kullanılabilir limit şablonu.</summary>
public sealed class ServicePlanDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int MaxSites { get; set; }

    public int MaxDiskMb { get; set; }

    /// <summary>En fazla veritabanı (Plesk MaxDatabases; 0 = sınırsız).</summary>
    public int MaxDatabases { get; set; }

    /// <summary>En fazla posta kutusu (Plesk MaxMailboxes; 0 = sınırsız).</summary>
    public int MaxMailboxes { get; set; }
}

public sealed class ServicePlanRequest
{
    public string Name { get; set; } = string.Empty;

    public int MaxSites { get; set; }

    public int MaxDiskMb { get; set; }

    public int MaxDatabases { get; set; }

    public int MaxMailboxes { get; set; }
}
