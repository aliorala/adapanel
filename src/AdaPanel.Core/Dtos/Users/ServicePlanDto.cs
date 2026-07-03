namespace AdaPanel.Core.Dtos.Users;

/// <summary>panel "Service Plan" karşılığı: müşterilere atanabilen yeniden kullanılabilir limit şablonu.</summary>
public sealed class ServicePlanDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int MaxSites { get; set; }

    public int MaxDiskMb { get; set; }

    /// <summary>En fazla veritabanı (panel MaxDatabases; 0 = sınırsız).</summary>
    public int MaxDatabases { get; set; }

    /// <summary>En fazla posta kutusu (panel MaxMailboxes; 0 = sınırsız).</summary>
    public int MaxMailboxes { get; set; }

    /// <summary>En fazla planlı görev (panel MaxScheduledTasks; 0 = sınırsız).</summary>
    public int MaxScheduledTasks { get; set; }
}

public sealed class ServicePlanRequest
{
    public string Name { get; set; } = string.Empty;

    public int MaxSites { get; set; }

    public int MaxDiskMb { get; set; }

    public int MaxDatabases { get; set; }

    public int MaxMailboxes { get; set; }

    public int MaxScheduledTasks { get; set; }
}
