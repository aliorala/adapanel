namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Ters DNS (rDNS / PTR) kontrol sonucu. PTR kaydi IP sahibinde (VPS saglayici) tutulur;
/// panel onu YAZAMAZ, yalnizca DOGRULAR ve yonlendirir (panel'in pratik yaklasimi).
/// </summary>
public sealed class RdnsCheckDto
{
    /// <summary>Kontrol edilen hostname (beklenen PTR degeri), orn. mail.example.com.</summary>
    public string ExpectedHostName { get; set; } = string.Empty;

    /// <summary>Hostname'in cozumlendigi IP (A kaydi).</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>IP'nin mevcut PTR (ters) kaydi; yoksa bos.</summary>
    public string PtrHostName { get; set; } = string.Empty;

    public bool HasPtr { get; set; }

    /// <summary>PTR, beklenen hostname ile eslesiyor mu.</summary>
    public bool Matches { get; set; }

    /// <summary>Forward-confirmed rDNS: PTR hostname'in A kaydi ayni IP'ye donuyor mu.</summary>
    public bool ForwardConfirmed { get; set; }

    /// <summary>Durum/yonlendirme i18n anahtari (mail.rdns.*).</summary>
    public string StatusMessage { get; set; } = string.Empty;
}
