namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Sunucu-geneli DNS şablonu kaydı (Plesk "DNS template" gibi). Yeni site bölgesi oluşturulurken bu kayıtlar
/// yer tutucular genişletilerek uygulanır. Yer tutucular: {domain} (zone adı), {ip} (sunucu IPv4),
/// {ns1}/{ns2} (ns1.domain / ns2.domain).
/// </summary>
public sealed class DnsTemplateRecordDto
{
    public string Type { get; set; } = "A";

    public string Host { get; set; } = "@";

    public string Value { get; set; } = string.Empty;

    public int Ttl { get; set; } = 3600;
}

/// <summary>Sunucu-geneli DNS şablonu (yeni bölgelerin varsayılan kayıt seti).</summary>
public sealed class DnsTemplateDto
{
    public List<DnsTemplateRecordDto> Records { get; set; } = [];
}

/// <summary>DNS şablonunu güncelleme isteği.</summary>
public sealed class UpdateDnsTemplateRequest
{
    public List<DnsTemplateRecordDto> Records { get; set; } = [];
}
