namespace AdaPanel.Core.Dtos.Licensing;

/// <summary>
/// Panelin doğrulama sunucusuna GÖNDERDİĞİ gövdenin TAMAMI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gizlilik sözleşmesi:</b> panel bu dört alandan başka HİÇBİR ŞEY göndermez — site listesi,
/// alan adı, müşteri sayısı, e-posta, IP, donanım envanteri yoktur. Parmak izi ham makine
/// kimliklerinin kendisi değil, onların SHA-256 özetidir (geri döndürülemez).
/// </para>
/// <para>
/// Alan eklemek isteyen: önce <c>docs/lisans-aktivasyon.md</c>'yi güncelle. Belgeyle kod arasındaki
/// fark, gizlilik vaadinin sessizce bozulması demektir.
/// </para>
/// </remarks>
public sealed class LicenseVerificationRequest
{
    /// <summary>Ürün kodu (aynı lisans sunucusu birden çok ürüne hizmet verebilsin diye).</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>Panelde saklı lisans anahtarı (düz metin — TLS'in altında).</summary>
    public string LicenseKey { get; init; } = string.Empty;

    /// <summary>Bu kurulumun kararlı parmak izi (SHA-256, 64 karakter onaltılık).</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>Panel sürümü (sunucu sürüm bazlı kural uygulayabilsin diye).</summary>
    public string PanelVersion { get; init; } = string.Empty;
}

/// <summary>
/// Doğrulama sunucusundan BEKLENEN yanıt.
/// </summary>
/// <remarks>
/// Yalnızca <b>HTTP 200 + geçerli JSON</b> bir KARAR sayılır. Bunun dışındaki her şey (ağ hatası,
/// zaman aşımı, 4xx, 5xx, bozuk gövde) "ulaşılamadı" olarak yorumlanır ve çevrimdışı toleransa
/// düşülür. Sunucudaki bir yapılandırma hatasının müşterinin panelini kısıtlaması kabul edilemez.
/// </remarks>
public sealed class LicenseVerificationResponse
{
    /// <summary>"valid" | "invalid" | "expired". Tanınmayan değer "invalid" sayılmaz, ULAŞILAMADI sayılır.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Ürün kademesi (serbest metin; panelde yalnız gösterilir).</summary>
    public string Sku { get; init; } = string.Empty;

    /// <summary>Bu lisansın izin verdiği en fazla abonelik (0 ya da alan yok = sınırsız).</summary>
    public int MaxSubscriptions { get; init; }

    /// <summary>Lisans bitiş tarihi (UTC); süresizse null.</summary>
    public DateTime? ExpiresAtUtc { get; init; }

    /// <summary>İsteğe bağlı serbest metin (destek mesajı); panelde teşhis alanında gösterilir.</summary>
    public string Message { get; init; } = string.Empty;
}
