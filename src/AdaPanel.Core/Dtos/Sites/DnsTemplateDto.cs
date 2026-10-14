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

    /// <summary>MX (ve SRV) için öncelik. Diğer tiplerde yok sayılır.</summary>
    public int? Priority { get; set; }
}

/// <summary>Sunucu-geneli DNS şablonu (yeni bölgelerin varsayılan kayıt seti).</summary>
public sealed class DnsTemplateDto
{
    public List<DnsIpMappingDto> IpMappings { get; set; } = [];
    public List<DnsIpMappingResultDto> MappingResults { get; set; } = [];
    public List<DnsTemplateRecordDto> Records { get; set; } = [];

    /// <summary>
    /// SUNUCU-GENELİ ikincil (slave) DNS sunucularının IP'leri. Buradaki her IP, panelin birincil
    /// olduğu HER bölgede AXFR izni alır ve bölge değiştiğinde NOTIFY gönderilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plesk paritesi: Plesk'te bu iki ayrı yerde durur — "Slave DNS Manager" eklentisi ikincil
    /// sunucuyu tanımlar ve IP'sini otomatik olarak transfer listesine ekler, "DNS Template
    /// Settings → Transfer Restrictions Template" ise bu listeyi yeni bölgelere uygular.
    /// AdaPanel'de ikisi tek yer: DNS şablonu.
    /// </para>
    /// <para>
    /// Neden sunucu geneli ŞART: bölge başına <c>ZoneTransferIps</c> zaten vardı, ama ikincil DNS
    /// altyapısı sunucunun tamamı için kurulur. Yüz alan adı varken IP'yi yüz kere elle girmek,
    /// er ya da geç unutulan bir bölge demektir — o bölge ikincil sunucuya HİÇ kopyalanmaz ve
    /// birincil düştüğünde yalnız o alan adı çözülmez. Sessiz, seçici ve fark edilmesi zor bir arıza.
    /// </para>
    /// </remarks>
    public List<string> SecondaryDnsServers { get; set; } = [];
}

/// <summary>DNS şablonunu güncelleme isteği.</summary>
public sealed class UpdateDnsTemplateRequest
{
    public List<DnsIpMappingDto>? IpMappings { get; set; }
    public bool ApplyIpMappingsToExistingZones { get; set; }
    public List<DnsTemplateRecordDto> Records { get; set; } = [];

    /// <summary>Sunucu-geneli ikincil (slave) DNS sunucu IP'leri; bkz. <see cref="DnsTemplateDto.SecondaryDnsServers"/>.</summary>
    public List<string> SecondaryDnsServers { get; set; } = [];
}

public sealed class DnsIpMappingDto
{
    public string PrivateAddress { get; set; } = "";
    public string PublicAddress { get; set; } = "";
}

public sealed class DnsIpMappingResultDto
{
    public string SiteName { get; set; } = "";
    public int ChangedRecords { get; set; }
    public bool Succeeded { get; set; }
    public string Error { get; set; } = "";
}
