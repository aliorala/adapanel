using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.PanelAccess;

public sealed class SetupPanelAccessRequest
{
    /// <summary>
    /// Yöneticinin KENDİ panel alan adı (ör. panel.ornek.com). Panel bir üründür; hiçbir alan adı
    /// koda gömülmez, bu değer daima yöneticiden gelir.
    /// </summary>
    [Required]
    [StringLength(253, MinimumLength = 4)]
    public string HostName { get; set; } = string.Empty;

    /// <summary>
    /// Let's Encrypt iletişim e-postası. Boşsa panelde daha önce kaydedilmiş ortak ACME e-postası
    /// (config/acme-contact.json) kullanılır; o da yoksa sertifika adımı net hata verir.
    /// </summary>
    [StringLength(256)]
    [EmailAddress]
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>true ise Let's Encrypt STAGING ortamı kullanılır (deneme; tarayıcıda güvenilmez görünür).</summary>
    public bool UseStaging { get; set; }
}
