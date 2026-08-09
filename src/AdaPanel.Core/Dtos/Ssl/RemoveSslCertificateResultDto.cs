namespace AdaPanel.Core.Dtos.Ssl;

/// <summary>
/// HTTPS bağlaması kaldırma işleminin SONUCU. Kullanıcı ne olduğunu tam olarak görmeli: hangi
/// bağlama gitti, "Force HTTPS" kapatıldı mı, sertifikaya ne oldu.
/// </summary>
public sealed class RemoveSslCertificateResultDto
{
    public string SiteName { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>Bağlamanın gerçekten kalktığı DOĞRULANDI mı (kaldırma sonrası yeniden ölçüldü).</summary>
    public bool BindingRemoved { get; set; }

    /// <summary>
    /// Sitenin SON HTTPS bağlaması kaldırıldığı için HTTP→HTTPS yönlendirmesi (Force HTTPS)
    /// kapatıldı mı. Kapatılmasaydı site tamamen erişilemez kalırdı.
    /// </summary>
    public bool HttpsRedirectDisabled { get; set; }

    /// <summary>Kaldırılan bağlamanın sertifika parmak izi (ölçülemediyse boş).</summary>
    public string CertificateThumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Sertifikaya ne yapıldı:
    /// <list type="bullet">
    /// <item><c>kept</c> — depoda bırakıldı (istenmedi; yeniden bağlamak için orada durur)</item>
    /// <item><c>kept_in_use</c> — İSTENDİ ama başka bağlama(lar) kullandığı için SİLİNMEDİ</item>
    /// <item><c>removed</c> — depodan silindi</item>
    /// <item><c>remove_failed</c> — silinmek istendi, silinemedi (ayrıntı: <see cref="Detail"/>)</item>
    /// </list>
    /// </summary>
    public string CertificateAction { get; set; } = string.Empty;

    /// <summary>Sertifikayı hâlâ kullanan diğer bağlamalar (site + bağlama bilgisi).</summary>
    public string[] CertificateUsedBy { get; set; } = [];

    /// <summary>Serbest metin ayrıntı (silme hatası vb.).</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>İşlem sonrası sitenin GÜNCEL SSL durumu (ekran tazelemek için).</summary>
    public SslWorkspaceDto Workspace { get; set; } = new();
}
