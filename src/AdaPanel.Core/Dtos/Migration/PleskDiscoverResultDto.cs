namespace AdaPanel.Core.Dtos.Migration;

/// <summary>Plesk'te keşfedilen bir domain (göç sihirbazında seçilince formu doldurur).</summary>
public sealed class PleskDomainDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Plesk barındırma türü (örn. virtual, none) — bilgilendirme amaçlı.</summary>
    public string HostingType { get; set; } = string.Empty;
}

/// <summary>Plesk keşif sonucu.</summary>
public sealed class PleskDiscoverResultDto
{
    public List<PleskDomainDto> Domains { get; set; } = [];
}
