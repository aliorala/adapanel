namespace AdaPanel.Core.Dtos.Mail;

/// <summary>Bir mail domaini için dış DNS sağlayıcısına eklenmesi gereken kayıt.</summary>
public sealed class MailDnsRecordDto
{
    public string Type { get; set; } = "TXT";

    /// <summary>Zone'a göre host adı ("@", "mail", "_dmarc", "selector._domainkey" gibi).</summary>
    public string Host { get; set; } = "@";

    public string Value { get; set; } = string.Empty;

    public int? Priority { get; set; }

    /// <summary>Kaydın amacı: a | mx | spf | dkim | dmarc.</summary>
    public string Purpose { get; set; } = string.Empty;
}

public sealed class MailDnsDto
{
    public string Domain { get; set; } = string.Empty;

    public string MailHost { get; set; } = string.Empty;

    /// <summary>Panel ayarlarındaki sunucu adresi (boşsa A kaydı değeri kullanıcıdan beklenir).</summary>
    public string ServerAddress { get; set; } = string.Empty;

    public MailDnsRecordDto[] Records { get; set; } = [];
}

public sealed class ApplyMailDnsResultDto
{
    public string ZoneName { get; set; } = string.Empty;

    /// <summary>
    /// Bölgeye GERÇEKTEN yazılan kayıt sayısı. Yayınlanmadıysa 0.
    /// </summary>
    /// <remarks>
    /// Eskiden bu alan "gönderilmesi düşünülen" kayıt sayısıydı: bölge ikincilken ya da Windows
    /// DNS rolü kurulu değilken de dolu geliyordu ve ekran "N kayıt uygulandı" yazıyordu. Doğru
    /// yazılmış bir sayı, yanlış bir gerçeği anlatıyordu.
    /// </remarks>
    public int AppliedCount { get; set; }

    /// <summary>Kayıtlar Windows DNS bölgesine yazıldı mı.</summary>
    public bool Published { get; set; }

    /// <summary>
    /// Sonucun çeviri anahtarı: <c>mail.dns_applied</c> ya da yayınlanmama sebebi
    /// (<c>mail.dns_not_published_*</c>). Ekran bunu doğrudan basar.
    /// </summary>
    public string StatusMessage { get; set; } = string.Empty;
}
