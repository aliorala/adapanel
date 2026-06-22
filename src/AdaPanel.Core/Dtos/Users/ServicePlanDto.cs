namespace AdaPanel.Core.Dtos.Users;

/// <summary>Plesk "Service Plan" karşılığı: müşterilere atanabilen yeniden kullanılabilir limit şablonu.</summary>
public sealed class ServicePlanDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int MaxSites { get; set; }

    public int MaxDiskMb { get; set; }
}

public sealed class ServicePlanRequest
{
    public string Name { get; set; } = string.Empty;

    public int MaxSites { get; set; }

    public int MaxDiskMb { get; set; }
}
