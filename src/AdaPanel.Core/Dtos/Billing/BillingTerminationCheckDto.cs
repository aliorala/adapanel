namespace AdaPanel.Core.Dtos.Billing;

/// <summary>
/// "Bu hesabı silmek GÜVENLİ mi?" sorusunun panel tarafındaki yanıtı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var:</b> WHMCS'in <c>TerminateAccount</c> işlevi hesabı ve — yapılandırılmışsa — bütün
/// sitelerini dosyalarıyla birlikte siler. Modül hesabı yalnızca KULLANICI ADIYLA buluyordu; ad
/// panelde elle değiştirilmiş ya da başka bir role ait bir hesapla çakışmışsa, iptal edilen hizmet
/// başka birinin verisini yok edebilirdi. Bu depoda aynı sınıftan bir hata daha önce gerçekleşti
/// (özyinelemeli silme müşteri sitesini silebiliyordu), o yüzden burada silme kararı ÖLÇÜLÜR.
/// </para>
/// <para>
/// <b>Karar panelde verilir, modülde değil:</b> doğrulamanın kanıtı (rol, atanmış alan adları)
/// paneldedir. PHP tarafında tekrarlansaydı iki kopya arasında sessiz bir sapma oluşabilirdi ve
/// yanlış tarafın kazandığı yer, geri alınamaz silme olurdu.
/// </para>
/// </remarks>
public sealed class BillingTerminationCheckDto
{
    /// <summary>Eşleşen panel kullanıcısının kimliği; eşleşme yoksa <c>null</c>.</summary>
    public Guid? UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Silme işlemi yapılabilir mi. <c>false</c> ise modül silmeyi YAPMAZ ve sebebi WHMCS'e yazar.
    /// </summary>
    public bool SafeToTerminate { get; set; }

    /// <summary>
    /// Kararın sabit sebebi — arayüz ve modül bunu çevirir:
    /// "ok" | "not_found" | "ambiguous" | "role_not_customer" | "domain_mismatch".
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Sahipliği kanıtlayan alan adı (eşleşme başarılıysa).</summary>
    public string MatchedDomain { get; set; } = string.Empty;
}
