namespace AdaPanel.Core.Dtos.Firewall;

/// <summary>
/// Tek bir hizmet portunun sunucu-yerel durumu: dinleniyor mu (ve hangi kapsamda),
/// Windows Firewall izin veriyor mu, bu port gerekli mi. Bulut sağlayıcı güvenlik
/// duvarı (security group/NSG) bu katmanın DIŞINDADIR ve yerelden tespit edilemez.
/// </summary>
public sealed class PortStatusDto
{
    public int Port { get; set; }

    /// <summary>Port bir aralıksa (ör. FTP pasif 50000-51000) bitiş portu; tek portsa 0.</summary>
    public int RangeEnd { get; set; }

    public string Protocol { get; set; } = "TCP";

    /// <summary>i18n anahtarı son eki: panel, http, https, ftp, smtp, submission, imap, ...</summary>
    public string ServiceKey { get; set; } = string.Empty;

    /// <summary>panel | web | mail | ftp | tools</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Hizmetin çalışması için bu portun açık olması gerekiyor mu.</summary>
    public bool Required { get; set; }

    /// <summary>Sunucuda bir süreç bu portu dinliyor mu (TCP listener tablosundan).</summary>
    public bool Listening { get; set; }

    public bool ListeningMeasured { get; set; } = true;

    /// <summary>all = tüm arabirimler/erişilebilir, loopback = yalnız 127.0.0.1, boş = dinlenmiyor.</summary>
    public string ListenScope { get; set; } = string.Empty;

    /// <summary>Etkin bir inbound "Allow" firewall kuralı bu portu kapsıyor mu.</summary>
    public bool FirewallAllowed { get; set; }

    public bool FirewallMeasured { get; set; } = true;
}
