namespace AdaPanel.Core.Dtos.Firewall;

public sealed class AddIpBlockRequest
{
    /// <summary>Tek IP (203.0.113.7) ya da CIDR (203.0.113.0/24, 2001:db8::/32).</summary>
    public string Target { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    /// <summary>Bos / 0 = suresiz. En fazla 525600 (1 yil). Sure dolunca engel OTOMATIK kaldirilir.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>
    /// Ulke engeli ISTENIRSE reddedilir: panelde GeoIP kaynagi yoktur ve uydurulmaz
    /// (<c>firewall.geoip_unavailable</c>).
    /// </summary>
    public string? CountryCode { get; set; }
}

public sealed class IpBlockEntryDto
{
    public string Id { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>null = suresiz.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string RuleName { get; set; } = string.Empty;

    /// <summary>
    /// "applied" = kural Windows Firewall'a yazildi ve GERI OKUNDU. "failed" = yazilamadi: IP agda
    /// ENGELLENMEDI (<see cref="RuleError"/>). Ikisi de durustce raporlanir.
    /// </summary>
    public string RuleState { get; set; } = "failed";

    public string RuleError { get; set; } = string.Empty;
}

public sealed class IpBlockListDto
{
    public IReadOnlyCollection<IpBlockEntryDto> Entries { get; set; } = [];

    /// <summary>Panelde GeoIP kaynagi yok; ulke engeli desteklenmez.</summary>
    public bool CountryBlockSupported { get; set; }
}
