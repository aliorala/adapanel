using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Abonelikten çıkma İSTEĞİ (birinci adım): "beni bu listeden çıkar" der, ama çıkarmaz —
/// adrese bir onay bağlantısı gönderilir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden iki adım:</b> Stalwart'ın dağıtım listesi fan-out'u iletiyi KİŞİSELLEŞTİRMEZ (ölçüldü:
/// teslim edilen iletide <c>List-Unsubscribe</c> yok, aboneye özel tek başlık teslim anında eklenen
/// <c>Delivered-To</c>). Dolayısıyla aboneye özel tek-tık bağlantı (RFC 8058) üretilemez; bağlantı
/// LİSTE başınadır. Tek adımlı olsaydı, bağlantıyı eline geçiren HERKES başkasını listeden
/// atabilirdi. Onay e-postası, isteği yapanın gerçekten O ADRESİN sahibi olduğunu kanıtlar.
/// Mailman/Plesk de aynı sebeple onay ister.
/// </para>
/// </remarks>
public sealed class MailListUnsubscribeRequest
{
    /// <summary>Liste adresi (ör. <c>duyuru@ornek.com</c>).</summary>
    [Required]
    [StringLength(320, MinimumLength = 3)]
    public string List { get; set; } = string.Empty;

    /// <summary>Listeden çıkarılmak istenen abone adresi.</summary>
    [Required]
    [StringLength(320, MinimumLength = 3)]
    public string Email { get; set; } = string.Empty;
}

/// <summary>Onay adımı: e-postayla gelen tek kullanımlık belirteç.</summary>
public sealed class MailListUnsubscribeConfirmRequest
{
    [Required]
    [StringLength(128, MinimumLength = 16)]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// Onay adımının sonucu.
/// </summary>
/// <remarks>
/// <b>Neden burada DÜRÜST cevap veriliyor (istek adımının aksine):</b> istek adımı "bu adres abone
/// mi" sorusunu cevaplamamalıdır, çünkü cevaplarsa uç bir e-posta hasat aracına döner. Onay adımında
/// böyle bir sızıntı YOKTUR: elinde belirteç olan kişi onu zaten o adrese gelen e-postadan almıştır
/// ve belirteç 256 bit rastgeledir — deneyerek adres taramak mümkün değildir. Burada da "başarılı"
/// demek, süresi geçmiş bağlantıya tıklayan kullanıcıya çıkarıldığını söylemek olurdu; oysa
/// çıkarılmamıştır. Panelin en pahalı dersi buydu: işlem yapmadan "tamam" demek.
/// </remarks>
public sealed class MailListUnsubscribeResultDto
{
    /// <summary>Abone gerçekten listeden çıkarıldı mı?</summary>
    public bool Removed { get; set; }

    /// <summary>Çıkarıldıysa hangi listeden (kullanıcıya gösterilir).</summary>
    public string List { get; set; } = string.Empty;

    /// <summary>Sonucu açıklayan çeviri anahtarı.</summary>
    public string Message { get; set; } = string.Empty;
}
